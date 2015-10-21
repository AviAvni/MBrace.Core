﻿namespace MBrace.Flow

open System
open Nessos.Streams

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Library
open MBrace.Library.CloudCollectionUtils

open MBrace.Flow
open MBrace.Flow.Internals

#nowarn "444"

type internal CloudCollection private () =

    /// <summary>
    ///     Creates a CloudFlow according to partitions of provided cloud collection.
    /// </summary>
    /// <param name="collection">Input cloud collection.</param>
    /// <param name="weight">Worker weighing function. Defaults to processor count.</param>
    /// <param name="sizeThresholdPerWorker">Restricts concurrent processing of collection partitions up to specified size per worker.</param>
    static member ToCloudFlow (collection : ICloudCollection<'T>, ?weight : IWorkerRef -> int, ?sizeThresholdPerWorker:unit -> int64) : CloudFlow<'T> =
        { new CloudFlow<'T> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<'T, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) = cloud {
                // Performs flow reduction on given input partition in a single MBrace work item
                let reducePartitionsInSingleWorkItem (partitions : ICloudCollection<'T> []) = local {
                    // further partition according to collection size threshold, if so specified.
                    let! partitionSlices = local {
                        match sizeThresholdPerWorker with
                        | None -> return [| partitions |]
                        | Some f -> return! partitions |> Partition.partitionBySize (fun p -> p.GetSizeAsync()) (f ()) |> Cloud.OfAsync
                    }

                    // compute a single partition
                    let computePartitionSlice (slice : ICloudCollection<'T> []) = local {
                        let! collector = collectorf
                        let! seqs = slice |> Seq.map (fun p -> p.GetEnumerableAsync()) |> Async.Parallel |> Cloud.OfAsync
                        // Partial fix for performance issue when the number of slices is less than the number of cores
                        match seqs with
                        | [|col|] ->
                            let pStream = col |> ParStream.ofSeq 
                            let value = pStream.Apply (collector.ToParStreamCollector())
                            return! projection value
                        | _ ->
                            let pStream = seqs |> ParStream.ofArray |> ParStream.collect Stream.ofSeq
                            let value = pStream.Apply (collector.ToParStreamCollector())
                            return! projection value
                    }

                    // sequentially compute partitions
                    let! results = Local.Sequential.map computePartitionSlice partitionSlices
                    return! combiner results
                }
                
                let! isTargetedWorkerSupported = Cloud.IsTargetedWorkerSupported

                match collection with
                | :? ITargetedPartitionCollection<'T> as tpc when isTargetedWorkerSupported ->
                    // scheduling data is encapsulated in CloudCollection, partition according to this
                    let! assignedPartitions = CloudCollection.ExtractTargetedCollections [|tpc|] |> Cloud.OfAsync

                    // reassign partitions according to the available workers
                    let! assignedPartitions = local {
                        let! workerRefs = Cloud.GetAvailableWorkers()
                        let missingWorkerRefs = Set.difference (Set.ofArray (assignedPartitions |> Array.map fst))
                                                               (Set.ofArray workerRefs) 
                        let availableWorkerRefs = Set.difference (Set.ofArray workerRefs)
                                                                 (Set.ofArray (assignedPartitions |> Array.map fst)) |> Set.toArray
                        let result = 
                            let availableCounter = ref 0
                            [| for (w, cols) in assignedPartitions do 
                                if Set.contains w missingWorkerRefs then
                                    yield (availableWorkerRefs.[!availableCounter], cols)
                                    incr availableCounter
                                else
                                    yield (w, cols) |]

                        return result
                    }

                    if Array.isEmpty assignedPartitions then return! combiner [||] else
                    let! results =
                        assignedPartitions
                        |> Seq.map (fun (w,ps) -> reducePartitionsInSingleWorkItem ps, w)
                        |> Cloud.Parallel

                    return! combiner results
                
                | _ ->
                
                let! workers = Cloud.GetAvailableWorkers()
                let! collector = collectorf
                // sort by id to ensure deterministic scheduling
                let workers = workers |> Array.sortBy (fun w -> w.Id)
                let workers =
                    match collector.DegreeOfParallelism with
                    | None -> workers
                    | Some dp -> [| for i in 0 .. dp - 1 -> workers.[i % workers.Length] |]


                let! partitions = CloudCollection.ExtractPartitions collection |> Cloud.OfAsync
                let! partitionss = CloudCollection.PartitionBySize(partitions, workers, isTargetedWorkerSupported, ?weight = weight) |> Cloud.OfAsync
                if Array.isEmpty partitionss then return! combiner [||] else
                        
                let! results =
                    if isTargetedWorkerSupported then
                        partitionss
                        |> Seq.filter (not << Array.isEmpty << snd)
                        |> Seq.map (fun (w,partitions) -> reducePartitionsInSingleWorkItem partitions, w)
                        |> Cloud.Parallel
                    else
                        partitionss
                        |> Seq.filter (not << Array.isEmpty << snd)
                        |> Seq.map (reducePartitionsInSingleWorkItem << snd)
                        |> Cloud.Parallel

                return! combiner results
            }
        }