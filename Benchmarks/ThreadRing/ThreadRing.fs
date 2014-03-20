// Copyright (C) by Housemarque, Inc.

module ThreadRing

open Hopac
open Hopac.Infixes
open Hopac.Extensions
open Hopac.Job.Infixes
open System
open System.Diagnostics

module ChGive =
  let proc (name: int) (inCh: Ch<int>) (outCh: Ch<int>) (finishCh: Ch<int>) =
    Job.foreverServer
     (inCh >>= fun n ->
      if n <> 0 then
        outCh <-- n-1
      else
        finishCh <-- name)

  let mkChain n finishCh = Job.delay <| fun () ->
    let ch0 = ch ()
    seq {1 .. n}
    |> Seq.foldJob
        (fun chIn i ->
           let chOut = if i=n then ch0 else ch ()
           proc i chIn chOut finishCh >>% chOut)
        ch0

  let run n m p =
    printf " ChGive: "
    let timer = Stopwatch.StartNew ()
    let i =
      run
       (Job.delay <| fun () ->
        let ps = Array.create p n
        let finishCh = ch ()
        ps
        |> Seq.Con.iterJob (fun n ->
           mkChain n finishCh >>= fun ch ->
           ch <-- m) >>= fun () ->
        Seq.Con.mapJob (fun _ -> upcast finishCh) (seq {1 .. p}))
    let d = timer.Elapsed
    printf "%9.0f m/s - %fs\n"
     (float (p*m) / d.TotalSeconds) d.TotalSeconds

module ChSend =
  let proc (name: int) (inCh: Ch<int>) (outCh: Ch<int>) (finishCh: Ch<int>) =
    Job.foreverServer
     (inCh >>= fun n ->
      if n <> 0 then
        outCh <-+ n-1
      else
        finishCh <-- name)

  let mkChain n finishCh = Job.delay <| fun () ->
    let ch0 = ch ()
    seq {1 .. n}
    |> Seq.foldJob
        (fun chIn i ->
           let chOut = if i=n then ch0 else ch ()
           proc i chIn chOut finishCh >>% chOut)
        ch0

  let run n m p =
    printf " ChSend: "
    let timer = Stopwatch.StartNew ()
    let i =
      run
       (Job.delay <| fun () ->
        let ps = Array.create p n
        let finishCh = ch ()
        ps
        |> Seq.Con.iterJob (fun n ->
           mkChain n finishCh >>= fun ch ->
           ch <-- m) >>= fun () ->
        Seq.Con.mapJob (fun _ -> upcast finishCh) (seq {1 .. p}))
    let d = timer.Elapsed
    printf "%9.0f m/s - %fs\n"
     (float (p*m) / d.TotalSeconds) d.TotalSeconds

module MbSend =
  let proc (name: int)
           (inMS: Mailbox<int>)
           (outMS: Mailbox<int>)
           (finishCh: Ch<int>) =
    Job.foreverServer
     (inMS >>= fun n ->
      if n <> 0 then
        outMS <<-+ n-1
      else
        finishCh <-- name)

  let mkChain n finishCh = Job.delay <| fun () ->
    let ms0 = mb ()
    seq {1 .. n}
    |> Seq.foldJob
        (fun msIn i ->
           let msOut = if i=n then ms0 else mb ()
           proc i msIn msOut finishCh >>% msOut)
        ms0

  let run n m p =
    printf " MbSend: "
    let timer = Stopwatch.StartNew ()
    let i =
      run
       (Job.delay <| fun () ->
        let ps = Array.create p n
        let finishCh = ch ()
        ps
        |> Seq.Con.iterJob (fun n ->
           mkChain n finishCh >>= fun ms ->
           ms <<-+ m) >>= fun () ->
        Seq.Con.mapJob (fun _ -> upcast finishCh) (seq {1 .. p}))
    let d = timer.Elapsed
    printf "%9.0f m/s - %fs\n"
     (float (p*m) / d.TotalSeconds) d.TotalSeconds

module MPPost =
  type MbPr<'a> = MailboxProcessor<'a>

  let mkChain n (finishPr: MbPr<_>) : array<MbPr<_>> =
    let mbprs = Array.zeroCreate n
    for i = 0 to n-1 do
      mbprs.[i] <- new MbPr<_>(fun inbox ->
        let name = i+1
        let nextPr = mbprs.[(i+1) % n]
        async {
          while true do
            let! n = inbox.Receive ()
            do if n <> 0 then
                 nextPr.Post (n-1)
               else
                 finishPr.Post name
        })
    mbprs |> Array.iter (fun mbpr -> mbpr.Start ())
    mbprs

  let run n m p =
    printf " MPPost: "
    let timer = Stopwatch.StartNew ()
    use allDone = new System.Threading.ManualResetEventSlim ()
    let results = ResizeArray<_>()
    use finishPr = new MbPr<_>(fun inbox ->
      async {
        for i=1 to p do
          let! x = inbox.Receive ()
          do results.Add x
        do allDone.Set ()
      })
    finishPr.Start ()
    let chains = Array.init p (fun _ -> mkChain n finishPr)
    for i=0 to p-1 do
      chains.[i].[0].Post m
    allDone.Wait ()
    for i=0 to p-1 do
      for j=0 to n-1 do
        (chains.[i].[j] :> IDisposable).Dispose ()
    let d = timer.Elapsed
    printf "%9.0f m/s - %fs\n"
     (float (p*m) / d.TotalSeconds) d.TotalSeconds

let cleanup () =
  for i=1 to 5 do
    Runtime.GCSettings.LargeObjectHeapCompactionMode <- Runtime.GCLargeObjectHeapCompactionMode.CompactOnce
    GC.Collect ()
    Threading.Thread.Sleep 50

do for p in [1; Environment.ProcessorCount] do
     for l in [50003; 503; 53] do
       for n in [500; 500000; 50000000] do
         printf "\nWith %d rings of length %d passing %d msgs:\n\n" p l n
         if n <= 500000 then
           MPPost.run l n p ; cleanup ()
         ChGive.run l n p ; cleanup ()   
         MbSend.run l n p ; cleanup ()
         ChSend.run l n p ; cleanup ()
