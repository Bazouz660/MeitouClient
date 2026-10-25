using System.Runtime.InteropServices;

namespace Meitou.Launcher;

/// <summary>
/// A news video's sound: its Vorbis frames decoded by NVorbis, played through OpenAL Soft (the game's audio library, embedded like
/// Skia; <see cref="NativeLibraries"/>) as one streaming source. <see cref="Position"/> is the clock the pictures follow.
/// </summary>
sealed partial class VideoAudio : IDisposable
{
    const int BlockFrames = 2048, Blocks = 4;
    const int AlFormatMono16 = 0x1101, AlFormatStereo16 = 0x1103, AlBuffersProcessed = 0x1016, AlSourceState = 0x1010, AlPlaying = 0x1012,
        AlSampleOffset = 0x1025;

    readonly IntPtr device, context;
    readonly uint source;
    readonly uint[] buffers = new uint[Blocks];
    readonly NVorbis.StreamDecoder decoder;
    readonly int channels, outChannels;
    readonly Thread thread;
    volatile bool running = true;
    long playedFrames; // frames in buffers OpenAL has finished with
    readonly Lock clock = new();

    public int SampleRate { get; }

    VideoAudio(IntPtr device, IntPtr context, NVorbis.StreamDecoder decoder)
    {
        this.device = device;
        this.context = context;
        this.decoder = decoder;
        channels = decoder.Channels;
        outChannels = Math.Min(channels, 2);
        SampleRate = decoder.SampleRate;
        alGenSources(1, out source);
        alGenBuffers(Blocks, buffers);
        thread = new Thread(Run) { Name = "News video sound", IsBackground = true };
    }

    /// <summary>The Vorbis track's player, not yet started; null (silent video) when OpenAL or the stream cannot be opened.</summary>
    public static VideoAudio? TryOpen(WebmFile file)
    {
        if (file.AudioCodec != "A_VORBIS" || file.AudioHeaders.Count != 3) return null;
        IntPtr device = 0, context = 0;
        try
        {
            device = alcOpenDevice(null);
            if (device == 0) return null;
            context = alcCreateContext(device, 0);
            if (context == 0 || !alcMakeContextCurrent(context)) throw new InvalidOperationException("no OpenAL context");
            return new VideoAudio(device, context, Decoder(file));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or InvalidDataException
                                      or ArgumentException or IndexOutOfRangeException)
        {
            if (context != 0) alcDestroyContext(context);
            if (device != 0) alcCloseDevice(device);
            return null;
        }
    }

    /// <summary>The Vorbis track as samples (its headers first, then its frames).</summary>
    internal static NVorbis.StreamDecoder Decoder(WebmFile file) => new(new VorbisPackets(file)) { ClipSamples = true };

    public void Start() => thread.Start();

    /// <summary>How far the sound has played: from OpenAL's position in the playing buffer, so it is smooth between blocks.</summary>
    public TimeSpan Position
    {
        get
        {
            lock (clock)
            {
                alGetSourcei(source, AlSampleOffset, out int offset);
                return TimeSpan.FromSeconds((double)(playedFrames + offset) / SampleRate);
            }
        }
    }

    /// <summary>True once every sample has been played (or the stream ended early).</summary>
    public bool Finished { get; private set; }

    void Run()
    {
        var samples = new float[BlockFrames * channels];
        var pcm = new short[BlockFrames * outChannels];
        var free = new Queue<uint>(buffers);
        bool ended = false;
        while (running)
        {
            lock (clock)
            {
                alGetSourcei(source, AlBuffersProcessed, out int processed);
                for (int i = 0; i < processed; i++)
                {
                    var done = new uint[1];
                    alSourceUnqueueBuffers(source, 1, done);
                    alGetBufferi(done[0], 0x2004 /* AL_SIZE */, out int bytes);
                    playedFrames += bytes / (2 * outChannels);
                    free.Enqueue(done[0]);
                }
            }
            while (!ended && free.Count > 0)
            {
                int read;
                try { read = decoder.Read(samples, 0, samples.Length); }
                catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) { read = 0; }
                if (read <= 0) { ended = true; break; }
                int frames = read / channels;
                for (int f = 0; f < frames; f++)
                    for (int c = 0; c < outChannels; c++)
                        pcm[f * outChannels + c] = (short)Math.Clamp(samples[f * channels + c] * 32767f, -32768f, 32767f);
                var buffer = free.Dequeue();
                lock (clock)
                {
                    alBufferData(buffer, outChannels == 2 ? AlFormatStereo16 : AlFormatMono16, pcm, frames * outChannels * 2, SampleRate);
                    alSourceQueueBuffers(source, 1, [buffer]);
                }
            }
            alGetSourcei(source, AlSourceState, out int state);
            if (state != AlPlaying)
            {
                if (ended && free.Count == Blocks) { Finished = true; return; }
                alSourcePlay(source); // first start, or after running dry
            }
            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        running = false;
        if (thread.IsAlive) thread.Join();
        alSourceStop(source);
        alDeleteSources(1, [source]);
        alDeleteBuffers(Blocks, buffers);
        alcMakeContextCurrent(0);
        alcDestroyContext(context);
        alcCloseDevice(device);
        decoder.Dispose();
    }

    /// <summary>The track's three headers, then its frames, as NVorbis packets.</summary>
    sealed class VorbisPackets(WebmFile file) : NVorbis.Contracts.IPacketProvider
    {
        int next;
        int Count => 3 + file.AudioFrames.Count;

        public bool CanSeek => false;
        public int StreamSerial => 0;
        public NVorbis.Contracts.IPacket? GetNextPacket() => next < Count ? Packet(next++) : null;
        public NVorbis.Contracts.IPacket? PeekNextPacket() => next < Count ? Packet(next) : null;
        public long GetGranuleCount() => (long)((file.AudioFrames.Count > 0 ? file.AudioFrames[^1].Time.TotalSeconds : 0) * file.SampleRate);
        public long SeekTo(long granulePos, int preRoll, NVorbis.Contracts.GetPacketGranuleCount getPacketGranuleCount) => throw new NotSupportedException();

        Packet Packet(int index) =>
            new(index < 3 ? file.AudioHeaders[index] : file.AudioFrames[index - 3].Data) { IsEndOfStream = index == Count - 1 };
    }

    sealed class Packet(ReadOnlyMemory<byte> data) : NVorbis.DataPacket
    {
        int read;
        protected override int TotalBits => data.Length * 8;
        protected override int ReadNextByte() => read < data.Length ? data.Span[read++] : -1;

        public override void Reset()
        {
            read = 0;
            base.Reset();
        }
    }

    // OpenAL 1.1, the few calls a streaming source needs.
    const string Al = "openal";
    [LibraryImport(Al)] private static partial IntPtr alcOpenDevice([MarshalAs(UnmanagedType.LPStr)] string? name);
    [LibraryImport(Al)] private static partial IntPtr alcCreateContext(IntPtr device, IntPtr attributes);
    [LibraryImport(Al)] [return: MarshalAs(UnmanagedType.U1)] private static partial bool alcMakeContextCurrent(IntPtr context);
    [LibraryImport(Al)] private static partial void alcDestroyContext(IntPtr context);
    [LibraryImport(Al)] [return: MarshalAs(UnmanagedType.U1)] private static partial bool alcCloseDevice(IntPtr device);
    [LibraryImport(Al)] private static partial void alGenSources(int count, out uint source);
    [LibraryImport(Al)] private static partial void alDeleteSources(int count, uint[] sources);
    [LibraryImport(Al)] private static partial void alGenBuffers(int count, [Out] uint[] buffers);
    [LibraryImport(Al)] private static partial void alDeleteBuffers(int count, uint[] buffers);
    [LibraryImport(Al)] private static partial void alBufferData(uint buffer, int format, short[] data, int size, int frequency);
    [LibraryImport(Al)] private static partial void alGetBufferi(uint buffer, int param, out int value);
    [LibraryImport(Al)] private static partial void alSourceQueueBuffers(uint source, int count, uint[] buffers);
    [LibraryImport(Al)] private static partial void alSourceUnqueueBuffers(uint source, int count, [Out] uint[] buffers);
    [LibraryImport(Al)] private static partial void alGetSourcei(uint source, int param, out int value);
    [LibraryImport(Al)] private static partial void alSourcePlay(uint source);
    [LibraryImport(Al)] private static partial void alSourceStop(uint source);
}
