using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace FortniteChestTracker.Core;

// Read only outer metadata and the network-header chunk; never decompress older or live files.
internal sealed class ReplayMetadataReader() : Unreal.Core.ReplayReader<Replay>(null!, ParseMode.EventsOnly)
{
    protected override void OnExternalDataRead(uint channelIndex, IExternalData? externalData) { }
    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup) { }
    protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update) { }

    public Replay Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream);
    }

    public Replay Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        return Read(stream);
    }

    private Replay Read(Stream stream)
    {
        using var archive = new Unreal.Core.BinaryReader(stream);
        var result = ReadReplay(archive);
        if (archive.IsError) throw new InvalidDataException("Malformed replay metadata.");
        return result;
    }

    public override void ReadReplayChunks(FArchive archive)
    {
        if (Replay.Info.IsLive) return;
        while (!archive.AtEnd())
        {
            var type = (ReplayChunkType)archive.ReadUInt32();
            var size = archive.ReadInt32();
            if (size < 0 || !archive.CanRead(size)) throw new InvalidDataException("Replay chunk lies outside the file.");
            if (type == ReplayChunkType.Header)
            {
                using var header = new Unreal.Core.BinaryReader(archive.ReadMemory(size)) { ReplayVersion = Replay.Info.FileVersion };
                ReadReplayHeader(header);
                if (header.IsError) throw new InvalidDataException("Malformed network header.");
                return;
            }
            archive.Seek(size, SeekOrigin.Current);
        }
        throw new InvalidDataException("The replay has no network header.");
    }
}
