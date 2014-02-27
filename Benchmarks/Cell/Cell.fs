﻿module Cell

open Hopac
open Hopac.Job.Infixes
open Hopac.Extensions
open System
open System.IO
open System.Diagnostics
open System.Threading.Tasks

module HopacCell =
  type Request<'a> =
   | Get
   | Put of 'a

  type Cell<'a> = {
    reqCh: Ch<Request<'a>>
    replyCh: Ch<'a>
  }

  let put (c: Cell<'a>) (x: 'a) : Job<unit> =
    Ch.give c.reqCh (Put x)

  let get (c: Cell<'a>) : Job<'a> = Ch.give c.reqCh Get >>. Ch.take c.replyCh

  let create (x: 'a) : Job<Cell<'a>> = Job.delay <| fun () ->
    let c = {reqCh = Ch.Now.create (); replyCh = Ch.Now.create ()} // 32 + 2 * 40 = 112 bytes
    let rec server c x =
      Ch.take c.reqCh >>= function // + 32 + 16 + 32 = 192 bytes
       | Get ->
         Ch.give c.replyCh x >>.
         server c x
       | Put x ->
         server c x
    Job.start (server c x) >>% c // + 32 = 224 bytes total for the cell server (64-bit).  That could be reduced a bit by inlining >>=.

  let run nCells nJobs nUpdates =
    printf "Hopac: "
    let timer = Stopwatch.StartNew ()
    let cells = Array.zeroCreate nCells
    let before = GC.GetTotalMemory true
    Job.Now.run <| job {
      do! Job.forUpTo 0 (nCells-1) <| fun i ->
            create i |>> fun cell -> cells.[i] <- cell
      do printf "%4d b/c " (max 0L (GC.GetTotalMemory true - before) / int64 nCells)
      return!
        seq {1 .. nJobs}
        |> Seq.map (fun _ ->
           let rnd = Random ()
           Job.forUpTo 1 nUpdates <| fun _ ->
             let c = rnd.Next (0, nCells)
             get cells.[c] >>= fun x ->
             put cells.[c] (x+1))
        |> Job.parIgnore
    }
    let d = timer.Elapsed
    let m = sprintf "%8.5f s to %d c * %d p * %d u\n"
             d.TotalSeconds nCells nJobs nUpdates
    printf "%s" m

module AsyncCell =
  type Request<'a> =
   | Get of AsyncReplyChannel<'a>
   | Put of 'a

  type Cell<'a> = MailboxProcessor<Request<'a>>

  let create (x: 'a) : Cell<'a> = MailboxProcessor.Start <| fun inbox ->
    let rec server x = async {
      let! req = inbox.Receive ()
      match req with
       | Get reply ->
         do reply.Reply x
         return! server x
       | Put x ->
         return! server x
    }
    server x

  let put (c: Cell<'a>) (x: 'a) : unit =
    c.Post (Put x)

  let get (c: Cell<'a>) : Async<'a> = c.PostAndAsyncReply Get

  let run nCells nJobs nUpdates =
    printf "Async: "
    let timer = Stopwatch.StartNew ()
    let cells = Array.zeroCreate nCells
    let before = GC.GetTotalMemory true
    for i=0 to nCells-1 do
      cells.[i] <- create i
    printf "%4d b/c " (max 0L (GC.GetTotalMemory true - before) / int64 nCells)
    seq {1 .. nJobs}
    |> Seq.map (fun _ -> async {
       let rnd = Random ()
       for i=0 to nUpdates do
         let c = rnd.Next (0, nCells)
         let! x = get cells.[c]
         do put cells.[c] (x+1)
       })
    |> Async.Parallel
    |> Async.RunSynchronously
    |> ignore
    for i=0 to nCells-1 do
      (cells.[i] :> IDisposable).Dispose ()
    let d = timer.Elapsed
    let m = sprintf "%8.5f s to %d c * %d p * %d u\n"
             d.TotalSeconds nCells nJobs nUpdates
    printf "%s" m

let tick () =
  for i=1 to 10 do
    Runtime.GCSettings.LargeObjectHeapCompactionMode <- Runtime.GCLargeObjectHeapCompactionMode.CompactOnce
    GC.Collect ()
    Threading.Thread.Sleep 50

let test m n p =
  HopacCell.run m n p ; tick ()
  AsyncCell.run m n p ; tick ()

do tick ()
   test 10 10 10
   test 100 100 100
   test 1000 1000 1000
   test 1000 1000 10000
   test 10000 1000 1000
   test 1000 10000 1000
   test 10000 10000 1000
   test 1000000 100000 100
   test 10000 10000 10000