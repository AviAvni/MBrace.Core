﻿namespace MBrace

open System
open System.Runtime.Serialization
open System.IO

open MBrace
open MBrace.Store
open MBrace.Continuation

#nowarn "444"

type private CloudRefHeader = { Type : Type ; UUID : string }

/// Represents an immutable reference to an
/// object that is persisted in the underlying store.
/// Cloud references cached locally for performance.
[<Sealed; DataContract; StructuredFormatDisplay("{StructuredFormatDisplay}")>]
type CloudRef<'T> =

    // https://visualfsharp.codeplex.com/workitem/199
    [<DataMember(Name = "Path")>]
    val mutable private path : string
    [<DataMember(Name = "UUID")>]
    val mutable private uuid : string
    [<DataMember(Name = "Serializer")>]
    val mutable private serializer : ISerializer option

    internal new (uuid, path, serializer) =
        { path = path ; uuid = uuid ; serializer = serializer }

    /// Asynchronously dereferences the cloud ref.
    member r.GetValue (?cache : bool) = cloud {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration>()
        return! Cloud.OfAsync <| async {
            match config.Cache.TryFind r.uuid with
            | Some v -> return v
            | None ->
                let serializer = match r.serializer with Some s -> s | None -> config.Serializer
                use! stream = config.FileStore.BeginRead r.path
                // consume header
                let _ = serializer.Deserialize<CloudRefHeader>(stream, leaveOpen = false)
                // consume payload
                let v = serializer.Deserialize<'T>(stream, leaveOpen = true)
                if defaultArg cache false then
                    ignore <| config.Cache.TryAdd(r.uuid, v)
                return v
        }
    }

    member r.Value = r.GetValue()

    member r.Cache() = r.GetValue(cache = true) |> Cloud.Ignore

    /// Returns size of cloud ref in bytes
    member r.GetSize () = cloud {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration>()
        return! Cloud.OfAsync <| config.FileStore.GetFileSize r.path
    }

    override r.ToString() = sprintf "CloudRef[%O] at %s" typeof<'T> r.path
    member private r.StructuredFormatDisplay = r.ToString()

    interface ICloudDisposable with
        member r.Dispose () = cloud {
            let! config = Cloud.GetResource<CloudFileStoreConfiguration>()
            return! Cloud.OfAsync <| config.FileStore.DeleteFile r.path
        }

    interface ICloudStorageEntity with
        member r.Type = sprintf "cloudref:%O" typeof<'T>
        member r.Id = r.path

#nowarn "444"

type CloudRef =
    
    /// <summary>
    ///     Creates a new cloud reference to the underlying store with provided value.
    ///     Cloud references are immutable and cached locally for performance.
    /// </summary>
    /// <param name="value">Cloud reference value.</param>
    /// <param name="directory">FileStore directory used for cloud ref. Defaults to execution context setting.</param>
    /// <param name="serializer">Serialization used for object serialization. Defaults to runtime context.</param>
    static member New(value : 'T, ?directory : string, ?serializer : ISerializer) = cloud {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration>()
        let directory = defaultArg directory config.DefaultDirectory
        let _serializer = match serializer with Some s -> s | None -> config.Serializer
        return! Cloud.OfAsync <| async {
            let uuid = Guid.NewGuid().ToString()
            let path = config.FileStore.GetRandomFilePath directory
            let writer (stream : Stream) = async {
                // write header
                _serializer.Serialize(stream, { Type = typeof<'T> ; UUID = uuid }, leaveOpen = true)
                // write value
                _serializer.Serialize(stream, value, leaveOpen = false)
            }
            do! config.FileStore.Write(path, writer)
            return new CloudRef<'T>(uuid, path, serializer)
        }
    }

    /// <summary>
    ///     Parses a cloud ref of given type with provided serializer. If successful, returns the cloud ref instance.
    /// </summary>
    /// <param name="path">Path to cloud ref.</param>
    /// <param name="serializer">Serializer for cloud ref.</param>
    static member Parse<'T>(path : string, ?serializer : ISerializer) = cloud {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration>()
        let _serializer = match serializer with Some s -> s | None -> config.Serializer
        return! Cloud.OfAsync <| async {
            use! stream = config.FileStore.BeginRead path
            let header = 
                try _serializer.Deserialize<CloudRefHeader>(stream, leaveOpen = false)
                with e -> raise <| new FormatException("Error reading cloud ref header.", e)
            return
                if header.Type = typeof<'T> then
                    new CloudRef<'T>(header.UUID, path, serializer)
                else
                    let msg = sprintf "expected cloudref of type %O but was %O." typeof<'T> header.Type
                    raise <| new InvalidDataException(msg)
        }
    }

    /// <summary>
    ///     Dereference a Cloud reference.
    /// </summary>
    /// <param name="cloudRef">CloudRef to be dereferenced.</param>
    static member Read(cloudRef : CloudRef<'T>) : Cloud<'T> = cloudRef.GetValue()

    /// <summary>
    ///     Cache a cloud reference to local execution context
    /// </summary>
    /// <param name="cloudRef">Cloud ref input</param>
    static member Cache(cloudRef : CloudRef<'T>) : Cloud<unit> = cloudRef.Cache()