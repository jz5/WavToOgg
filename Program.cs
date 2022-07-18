using OggVorbisEncoder;
const int writeBufferSize = 512;

// args[]: Linear PCM .wav file paths
foreach (var arg in args)
{
    if (Path.GetExtension(arg).ToLowerInvariant() != ".wav") continue;

    // Input WAV
    using var br = new BinaryReader(new FileStream(arg, FileMode.Open));
    var (format, data) = ReadWaveFile(br);
    if (format == null || data == null)
        continue;

    // Convert to Ogg
    var oggBytes = ConvertRawPcmFile(
        format.SampleRate,
        format.Channels,
        data,
        format.BitsPerSample == 16 ? PcmSample.SixteenBit : PcmSample.EightBit,
        format.SampleRate,
        format.Channels);

    // Output Ogg
    var file = arg[..(arg.Length - 4)] + ".ogg";
    Console.WriteLine(file);
    File.WriteAllBytes(file, oggBytes);
}


(WaveFormat?, byte[]?) ReadWaveFile(BinaryReader reader)
{
    WaveFormat? waveFormat = null;
    byte[]? pcmBytes = null;

    while (reader.BaseStream.Position != reader.BaseStream.Length)
    {
        var chunkId = new string(reader.ReadChars(4));
        var size = reader.ReadInt32();
        if (chunkId == "RIFF")
        {
            reader.ReadChars(4); // "WAVE"
        }
        else if (chunkId == "fmt ")
        {
            waveFormat = new WaveFormat
            {
                FormatTag = reader.ReadUInt16(),
                Channels = reader.ReadUInt16(),
                SampleRate = reader.ReadInt32(),
                AverageBytesPerSecond = reader.ReadInt32(),
                BlockAlign = reader.ReadUInt16(),
                BitsPerSample = reader.ReadUInt16(),
            };

            if (waveFormat.FormatTag != 0x1) // 0x1: Linear PCM
                throw new NotSupportedException();

            if (size - 16 > 0)
                reader.ReadBytes(size - 16);
        }
        else if (chunkId == "data")
        {
            pcmBytes = reader.ReadBytes(size);
            break;
        }
        else
        {
            reader.ReadBytes((size + 1) / 2 * 2);
        }
    }

    return (waveFormat, pcmBytes);
}

// Sample code from .NET Ogg Vorbis Encoder
// https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder
// MIT License
// Copyright(c) 2016 Steve Lillis 
// https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder/blob/master/LICENSE

byte[] ConvertRawPcmFile(int outputSampleRate, int outputChannels, IReadOnlyList<byte> pcmSamples, PcmSample pcmSampleSize, int pcmSampleRate, int pcmChannels)
{
    var numPcmSamples = (pcmSamples.Count / (int)pcmSampleSize / pcmChannels);
    var pcmDuration = numPcmSamples / (float)pcmSampleRate;

    var numOutputSamples = (int)(pcmDuration * outputSampleRate);
    //Ensure that sample buffer is aligned to write chunk size
    numOutputSamples = (numOutputSamples / writeBufferSize) * writeBufferSize;

    var outSamples = new float[outputChannels][];

    for (var ch = 0; ch < outputChannels; ch++)
    {
        outSamples[ch] = new float[numOutputSamples];
    }

    for (var sampleNumber = 0; sampleNumber < numOutputSamples; sampleNumber++)
    {
        var rawSample = 0.0f;

        for (var ch = 0; ch < outputChannels; ch++)
        {
            var sampleIndex = (sampleNumber * pcmChannels) * (int)pcmSampleSize;

            if (ch < pcmChannels) sampleIndex += (ch * (int)pcmSampleSize);

            rawSample = pcmSampleSize switch
            {
                PcmSample.EightBit => ByteToSample(pcmSamples[sampleIndex]),
                PcmSample.SixteenBit => ShortToSample(
                    (short)(pcmSamples[sampleIndex + 1] << 8 | pcmSamples[sampleIndex])),
                _ => rawSample
            };

            outSamples[ch][sampleNumber] = rawSample;
        }
    }

    return GenerateFile(outSamples, outputSampleRate, outputChannels);
}

byte[] GenerateFile(float[][] floatSamples, int sampleRate, int channels)
{
    using var outputData = new MemoryStream();

    // Stores all the static vorbis bitstream settings
    var info = VorbisInfo.InitVariableBitRate(channels, sampleRate, 0.5f);

    // set up our packet->stream encoder
    var serial = new Random().Next();
    var oggStream = new OggStream(serial);

    // =========================================================
    // HEADER
    // =========================================================
    // Vorbis streams begin with three headers; the initial header (with
    // most of the codec setup parameters) which is mandated by the Ogg
    // bitstream spec.  The second header holds any comment fields.  The
    // third header holds the bitstream codebook.

    var comments = new Comments();
    //comments.AddTag("ARTIST", "TEST");

    var infoPacket = HeaderPacketBuilder.BuildInfoPacket(info);
    var commentsPacket = HeaderPacketBuilder.BuildCommentsPacket(comments);
    var booksPacket = HeaderPacketBuilder.BuildBooksPacket(info);

    oggStream.PacketIn(infoPacket);
    oggStream.PacketIn(commentsPacket);
    oggStream.PacketIn(booksPacket);

    // Flush to force audio data onto its own page per the spec
    FlushPages(oggStream, outputData, true);

    // =========================================================
    // BODY (Audio Data)
    // =========================================================
    var processingState = ProcessingState.Create(info);

    for (var readIndex = 0; readIndex <= floatSamples[0].Length; readIndex += writeBufferSize)
    {
        if (readIndex == floatSamples[0].Length)
        {
            processingState.WriteEndOfStream();
        }
        else
        {
            processingState.WriteData(floatSamples, writeBufferSize, readIndex);
        }

        while (!oggStream.Finished && processingState.PacketOut(out var packet))
        {
            oggStream.PacketIn(packet);

            FlushPages(oggStream, outputData, false);
        }
    }

    FlushPages(oggStream, outputData, true);

    return outputData.ToArray();
}

static void FlushPages(OggStream oggStream, Stream output, bool force)
{
    while (oggStream.PageOut(out var page, force))
    {
        output.Write(page.Header, 0, page.Header.Length);
        output.Write(page.Body, 0, page.Body.Length);
    }
}

static float ByteToSample(short pcmValue) => pcmValue / 128f;

static float ShortToSample(short pcmValue) => pcmValue / 32768f;


internal enum PcmSample
{
    EightBit = 1,
    SixteenBit = 2
}


internal class WaveFormat
{
    public ushort FormatTag { get; set; }
    public ushort Channels { get; set; }
    public int SampleRate { get; set; }
    public int AverageBytesPerSecond { get; set; }
    public ushort BlockAlign { get; set; }
    public ushort BitsPerSample { get; set; }
}
