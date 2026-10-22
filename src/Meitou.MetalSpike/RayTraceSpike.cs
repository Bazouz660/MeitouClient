using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;

// A ray-tracing probe, isolated from the game: build one Metal acceleration structure, trace
// camera rays for a debug image, then trace the GI's probe workload and report its GPU cost.
// This measures the risky part (Metal traversal and the DDGI-sized ray batch) on this Mac.
static class RayTraceSpike
{
    const int TerrainCells = 512;          // 512² quads -> 524,288 triangles
    const int ProbeColumns = 32, ProbeLayers = 8, ProbeCascades = 2;
    const int ProbeCount = ProbeColumns * ProbeColumns * ProbeLayers * ProbeCascades;

    public static void Run()
    {
        var device = MTLDevice.CreateSystemDefaultDevice();
        Console.WriteLine($"rt device     {device.Name}");
        Console.WriteLine($"rt support    {device.SupportsRaytracing}");
        if (!device.SupportsRaytracing)
        {
            Console.WriteLine("rt            off: this Metal device does not support ray tracing");
            return;
        }
        Console.WriteLine($"rt memory     {device.RecommendedMaxWorkingSetSize / (1024.0 * 1024.0):0} MB recommended");

        var queue = device.NewCommandQueue();
        var (vertices, indices, triangles) = CreateTerrain(device);
        Console.WriteLine($"rt scene      {TerrainCells}² terrain, {triangles:N0} triangles, {vertices.Length / 1024:N0} KB vertices, {indices.Length / 1024:N0} KB indices");

        var (acceleration, sizes, buildMs) = BuildAccelerationStructure(device, queue, vertices, indices);
        Console.WriteLine($"rt accel      {sizes.accelerationStructureSize / (1024.0 * 1024.0):0.0} MB, scratch {sizes.buildScratchBufferSize / (1024.0 * 1024.0):0.0} MB, build {buildMs:0.0} ms");

        var library = CompileShader(device);
        var probePipeline = CreatePipeline(device, library, "rt_probes");
        var cameraPipeline = CreatePipeline(device, library, "rt_camera");

        Console.WriteLine();
        Console.WriteLine("Probe workload (the GI trace; 32 rays/probe, 1/4 of probes per frame):");
        var high = MeasureProbes(device, queue, probePipeline, acceleration, vertices, indices,
            raysPerProbe: 32, phases: 4, phase: 0, iterations: 12);
        PrintResult("  32 rays, 1/4 frame", high);

        Console.WriteLine();
        Console.WriteLine("Lower-cost probe tiers:");
        var medium = MeasureProbes(device, queue, probePipeline, acceleration, vertices, indices,
            raysPerProbe: 16, phases: 8, phase: 0, iterations: 16);
        PrintResult("  16 rays, 1/8 frame", medium);

        var low = MeasureProbes(device, queue, probePipeline, acceleration, vertices, indices,
            raysPerProbe: 16, phases: 16, phase: 0, iterations: 24);
        PrintResult("  16 rays, 1/16 frame", low);

        Console.WriteLine();
        var camera = MeasureCamera(device, queue, cameraPipeline, acceleration, vertices, indices,
            width: 1280, height: 720, iterations: 8);
        PrintResult("camera 1280x720", camera);

        var debugPath = Path.Combine(Path.GetTempPath(), "meitou-rt-spike.ppm");
        var debug = RenderCamera(device, queue, cameraPipeline, acceleration, vertices, indices,
            width: 640, height: 400);
        File.WriteAllBytes(debugPath, debug.Ppm);
        Console.WriteLine();
        Console.WriteLine($"rt debug      {debugPath} ({debug.HitPixels:N0}/{debug.TotalPixels:N0} hit pixels, {debug.MeanLuma:0.000} mean luma)");

        Console.WriteLine();
        Console.WriteLine("Interpretation: this is one primitive acceleration structure over terrain only.");
        Console.WriteLine("It is a lower bound for the real scene (no objects, foliage, TLAS, refit or probe resolve).");
    }

    static (MTLBuffer Vertices, MTLBuffer Indices, int Triangles) CreateTerrain(MTLDevice device)
    {
        int side = TerrainCells + 1;
        var vertices = new float[side * side * 3];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int i = (z * side + x) * 3;
            float wx = (x - TerrainCells * 0.5f) * 16.0f;
            float wz = (z - TerrainCells * 0.5f) * 16.0f;
            float h = 12.0f * MathF.Sin(wx * 0.003f) * MathF.Cos(wz * 0.0025f)
                    + 5.0f * MathF.Sin((wx + wz) * 0.0012f)
                    - 4.0f;
            vertices[i + 0] = wx;
            vertices[i + 1] = h;
            vertices[i + 2] = wz;
        }

        var indices = new int[TerrainCells * TerrainCells * 6];
        int n = 0;
        for (int z = 0; z < TerrainCells; z++)
        for (int x = 0; x < TerrainCells; x++)
        {
            int i0 = z * side + x;
            int i1 = i0 + 1;
            int i2 = i0 + side;
            int i3 = i2 + 1;
            indices[n++] = i0; indices[n++] = i2; indices[n++] = i1;
            indices[n++] = i1; indices[n++] = i2; indices[n++] = i3;
        }

        var vertexBuffer = device.NewBuffer((ulong)(vertices.Length * 4), MTLResourceOptions.ResourceStorageModeShared);
        vertexBuffer.Label = (NSString)"rt terrain vertices";
        Marshal.Copy(vertices, 0, vertexBuffer.Contents, vertices.Length);
        vertexBuffer.DidModifyRange(new NSRange { location = 0, length = (ulong)(vertices.Length * 4) });

        var indexBuffer = device.NewBuffer((ulong)(indices.Length * 4), MTLResourceOptions.ResourceStorageModeShared);
        indexBuffer.Label = (NSString)"rt terrain indices";
        Marshal.Copy(indices, 0, indexBuffer.Contents, indices.Length);
        indexBuffer.DidModifyRange(new NSRange { location = 0, length = (ulong)(indices.Length * 4) });

        return (vertexBuffer, indexBuffer, indices.Length / 3);
    }

    static (MTLAccelerationStructure Acceleration, MTLAccelerationStructureSizes Sizes, double BuildMs) BuildAccelerationStructure(
        MTLDevice device, MTLCommandQueue queue, MTLBuffer vertexBuffer, MTLBuffer indexBuffer)
    {
        var geometry = new MTLAccelerationStructureTriangleGeometryDescriptor
        {
            VertexBuffer = vertexBuffer,
            VertexBufferOffset = 0,
            VertexStride = 12,
            VertexFormat = MTLAttributeFormat.Float3,
            IndexBuffer = indexBuffer,
            IndexBufferOffset = 0,
            IndexType = MTLIndexType.UInt32,
            TriangleCount = (ulong)(indexBuffer.Length / 4 / 3),
            Opaque = true,
        };

        var descriptor = new MTLPrimitiveAccelerationStructureDescriptor();
        descriptor.GeometryDescriptors = NSArray.Array(new NSObject(geometry.NativePtr));
        descriptor.Usage = MTLAccelerationStructureUsage.PreferFastIntersection;

        var sizes = device.AccelerationStructureSizes(descriptor);
        var acceleration = device.NewAccelerationStructure(sizes.accelerationStructureSize);
        acceleration.Label = (NSString)"rt terrain acceleration";

        var scratch = device.NewBuffer(Math.Max(sizes.buildScratchBufferSize, 1), MTLResourceOptions.ResourceStorageModePrivate);
        scratch.Label = (NSString)"rt terrain scratch";

        var build = Stopwatch.StartNew();
        var command = queue.CommandBuffer();
        var encoder = command.AccelerationStructureCommandEncoder();
        encoder.BuildAccelerationStructure(acceleration, descriptor, scratch, 0);
        encoder.EndEncoding();
        command.Commit();
        command.WaitUntilCompleted();
        build.Stop();
        if (command.Status != MTLCommandBufferStatus.Completed)
            throw new InvalidOperationException($"acceleration structure build failed: {command.Status} {command.Error.LocalizedDescription}");
        return (acceleration, sizes, build.Elapsed.TotalMilliseconds);
    }

    static MTLLibrary CompileShader(MTLDevice device)
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "raytrace.metal"));
        var options = new MTLCompileOptions
        {
            LanguageVersion = MTLLanguageVersion.Version30,
            FastMathEnabled = true,
        };
        NSError error = default;
        var library = device.NewLibrary((NSString)source, options, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"ray-tracing shader compile failed: {error.LocalizedDescription}");
        return library;
    }

    static MTLComputePipelineState CreatePipeline(MTLDevice device, MTLLibrary library, string name)
    {
        NSError error = default;
        var function = library.NewFunction((NSString)name);
        var pipeline = device.NewComputePipelineState(function, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"{name} pipeline failed: {error.LocalizedDescription}");
        return pipeline;
    }

    static ProbeResult MeasureProbes(MTLDevice device, MTLCommandQueue queue, MTLComputePipelineState pipeline,
        MTLAccelerationStructure acceleration, MTLBuffer vertexBuffer, MTLBuffer indexBuffer,
        int raysPerProbe, int phases, int phase, int iterations)
    {
        int probes = ProbeCount / phases;
        int rays = probes * raysPerProbe;
        var parameters = new ProbeParameters
        {
            RaysPerProbe = (uint)raysPerProbe,
            ProbeCount = ProbeCount,
            PhaseCount = (uint)phases,
            Phase = (uint)phase,
        };
        var parameterBuffer = device.NewBuffer((ulong)Marshal.SizeOf<ProbeParameters>(), MTLResourceOptions.ResourceStorageModeShared);
        Marshal.StructureToPtr(parameters, parameterBuffer.Contents, false);
        parameterBuffer.DidModifyRange(new NSRange { location = 0, length = (ulong)Marshal.SizeOf<ProbeParameters>() });

        var hitBuffer = device.NewBuffer(4, MTLResourceOptions.ResourceStorageModeShared);
        Marshal.WriteInt32(hitBuffer.Contents, 0);
        hitBuffer.DidModifyRange(new NSRange { location = 0, length = 4 });

        // Warm up the pipeline and the acceleration structure's caches.
        RunProbeBatch(device, queue, pipeline, acceleration, vertexBuffer, indexBuffer, parameterBuffer, hitBuffer,
            rays, iterations: 2);
        ResetHitCount(hitBuffer);

        var samples = new List<(double GpuMs, double WallMs)>();
        for (int run = 0; run < 3; run++)
        {
            ResetHitCount(hitBuffer);
            samples.Add(RunProbeBatch(device, queue, pipeline, acceleration, vertexBuffer, indexBuffer,
                parameterBuffer, hitBuffer, rays, iterations));
        }
        int hits = Marshal.ReadInt32(hitBuffer.Contents) / iterations;
        samples.Sort((a, b) => a.GpuMs.CompareTo(b.GpuMs));
        var best = samples[0];
        return new ProbeResult(rays, hits, best.GpuMs, best.WallMs);
    }

    static (double GpuMs, double WallMs) RunProbeBatch(MTLDevice device, MTLCommandQueue queue,
        MTLComputePipelineState pipeline, MTLAccelerationStructure acceleration, MTLBuffer vertices, MTLBuffer indices,
        MTLBuffer parameters, MTLBuffer hits, int rays, int iterations)
    {
        var command = queue.CommandBuffer();
        var encoder = command.ComputeCommandEncoder();
        encoder.SetComputePipelineState(pipeline);
        encoder.SetBuffer(parameters, 0, 0);
        encoder.SetBuffer(vertices, 0, 1);
        encoder.SetBuffer(indices, 0, 2);
        encoder.SetAccelerationStructure(acceleration, 3);
        encoder.SetBuffer(hits, 0, 4);

        var threadgroup = new MTLSize { width = 64, height = 1, depth = 1 };
        var grid = new MTLSize { width = (ulong)rays, height = 1, depth = 1 };
        for (int i = 0; i < iterations; i++)
            encoder.DispatchThreads(grid, threadgroup);

        encoder.EndEncoding();

        var wall = Stopwatch.StartNew();
        command.Commit();
        command.WaitUntilCompleted();
        wall.Stop();
        if (command.Status != MTLCommandBufferStatus.Completed)
            throw new InvalidOperationException($"probe trace failed: {command.Status} {command.Error.LocalizedDescription}");

        double gpuMs = CommandBufferGpuMs(command, wall.Elapsed.TotalMilliseconds);
        return (gpuMs / iterations, wall.Elapsed.TotalMilliseconds / iterations);
    }

    static void ResetHitCount(MTLBuffer buffer)
    {
        Marshal.WriteInt32(buffer.Contents, 0);
        buffer.DidModifyRange(new NSRange { location = 0, length = 4 });
    }

    static ProbeResult MeasureCamera(MTLDevice device, MTLCommandQueue queue, MTLComputePipelineState pipeline,
        MTLAccelerationStructure acceleration, MTLBuffer vertices, MTLBuffer indices, int width, int height,
        int iterations)
    {
        var (texture, parameters) = CreateCameraResources(device, width, height);
        var command = queue.CommandBuffer();
        var encoder = command.ComputeCommandEncoder();
        encoder.SetComputePipelineState(pipeline);
        encoder.SetBuffer(parameters, 0, 0);
        encoder.SetBuffer(vertices, 0, 1);
        encoder.SetBuffer(indices, 0, 2);
        encoder.SetAccelerationStructure(acceleration, 3);
        encoder.SetTexture(texture, 0);
        var threadgroup = new MTLSize { width = 16, height = 16, depth = 1 };
        var grid = new MTLSize { width = (ulong)width, height = (ulong)height, depth = 1 };
        for (int i = 0; i < iterations; i++)
            encoder.DispatchThreads(grid, threadgroup);
        encoder.EndEncoding();

        var wall = Stopwatch.StartNew();
        command.Commit();
        command.WaitUntilCompleted();
        wall.Stop();
        if (command.Status != MTLCommandBufferStatus.Completed)
            throw new InvalidOperationException($"camera trace failed: {command.Status} {command.Error.LocalizedDescription}");

        double gpuMs = CommandBufferGpuMs(command, wall.Elapsed.TotalMilliseconds);
        int rays = width * height;
        return new ProbeResult(rays, 0, gpuMs / iterations, wall.Elapsed.TotalMilliseconds / iterations);
    }

    static (MTLTexture Texture, MTLBuffer Parameters) CreateCameraResources(MTLDevice device, int width, int height)
    {
        var descriptor = new MTLTextureDescriptor
        {
            TextureType = MTLTextureType.Type2D,
            PixelFormat = MTLPixelFormat.RGBA8Unorm,
            Width = (ulong)width,
            Height = (ulong)height,
            StorageMode = MTLStorageMode.Shared,
            Usage = MTLTextureUsage.ShaderWrite,
        };
        var texture = device.NewTexture(descriptor);
        texture.Label = (NSString)"rt camera target";

        var eye = new Vector3(0, 260, 760);
        var forward = Vector3.Normalize(new Vector3(0, 20, 0) - eye);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        var camera = new CameraParameters
        {
            Eye = new Vector4(eye, 0),
            Forward = new Vector4(forward, 0),
            Right = new Vector4(right, 0),
            Up = new Vector4(up, 0),
            TanHalfFov = MathF.Tan(60.0f * MathF.PI / 180.0f * 0.5f),
            Width = (uint)width,
            Height = (uint)height,
        };
        var buffer = device.NewBuffer((ulong)Marshal.SizeOf<CameraParameters>(), MTLResourceOptions.ResourceStorageModeShared);
        Marshal.StructureToPtr(camera, buffer.Contents, false);
        buffer.DidModifyRange(new NSRange { location = 0, length = (ulong)Marshal.SizeOf<CameraParameters>() });
        return (texture, buffer);
    }

    static (byte[] Ppm, int HitPixels, int TotalPixels, float MeanLuma) RenderCamera(MTLDevice device,
        MTLCommandQueue queue, MTLComputePipelineState pipeline, MTLAccelerationStructure acceleration,
        MTLBuffer vertices, MTLBuffer indices, int width, int height)
    {
        var (texture, parameters) = CreateCameraResources(device, width, height);
        var command = queue.CommandBuffer();
        var encoder = command.ComputeCommandEncoder();
        encoder.SetComputePipelineState(pipeline);
        encoder.SetBuffer(parameters, 0, 0);
        encoder.SetBuffer(vertices, 0, 1);
        encoder.SetBuffer(indices, 0, 2);
        encoder.SetAccelerationStructure(acceleration, 3);
        encoder.SetTexture(texture, 0);
        encoder.DispatchThreads(new MTLSize { width = (ulong)width, height = (ulong)height, depth = 1 },
            new MTLSize { width = 16, height = 16, depth = 1 });
        encoder.EndEncoding();
        command.Commit();
        command.WaitUntilCompleted();
        if (command.Status != MTLCommandBufferStatus.Completed)
            throw new InvalidOperationException($"debug trace failed: {command.Status} {command.Error.LocalizedDescription}");

        var rgba = new byte[width * height * 4];
        var region = new MTLRegion
        {
            origin = new MTLOrigin { x = 0, y = 0, z = 0 },
            size = new MTLSize { width = (ulong)width, height = (ulong)height, depth = 1 },
        };
        unsafe
        {
            fixed (byte* p = rgba)
                texture.GetBytes((IntPtr)p, (ulong)(width * 4), region, 0);
        }

        int hits = 0;
        double luma = 0;
        for (int i = 0; i < width * height; i++)
        {
            int p = i * 4;
            luma += (rgba[p] + rgba[p + 1] + rgba[p + 2]) / (3.0 * 255.0);
            if (rgba[p + 3] > 127)
                hits++;
        }

        using var stream = new MemoryStream();
        var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        stream.Write(header);
        for (int i = 0; i < width * height; i++)
        {
            stream.WriteByte(rgba[i * 4 + 0]);
            stream.WriteByte(rgba[i * 4 + 1]);
            stream.WriteByte(rgba[i * 4 + 2]);
        }
        return (stream.ToArray(), hits, width * height, (float)(luma / (width * height)));
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern double objc_msgSend_Double(IntPtr receiver, IntPtr selector);

    static double CommandBufferGpuMs(MTLCommandBuffer command, double wallMs)
    {
        // SharpMetal types the deprecated GPUStartTime/GPUEndTime as IntPtr; call the selectors
        // with the correct double ABI instead. Fall back to wall time if the OS returns nothing.
        var startSelector = new Selector("GPUStartTime").SelPtr;
        var endSelector = new Selector("GPUEndTime").SelPtr;
        double start = objc_msgSend_Double(command.NativePtr, startSelector);
        double end = objc_msgSend_Double(command.NativePtr, endSelector);
        double total = (end - start) * 1000.0;
        return double.IsFinite(total) && total > 0 && total < wallMs * 10 ? total : wallMs;
    }

    static void PrintResult(string name, ProbeResult result)
    {
        Console.WriteLine($"{name,-20} {result.GpuMs,7:0.00} ms   {result.WallMs,7:0.00} ms wall   " +
                          $"{result.Rays,10:N0} rays   {result.Rays / result.GpuMs / 1000.0,6:0.0} Mray/s" +
                          (result.Hits > 0 ? $"   {result.Hits:N0} hit rays/frame" : ""));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CameraParameters
    {
        public Vector4 Eye;
        public Vector4 Forward;
        public Vector4 Right;
        public Vector4 Up;
        public float TanHalfFov;
        public uint Width;
        public uint Height;
        public uint Pad4;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProbeParameters
    {
        public uint RaysPerProbe;
        public uint ProbeCount;
        public uint PhaseCount;
        public uint Phase;
    }

    readonly record struct ProbeResult(int Rays, int Hits, double GpuMs, double WallMs);
}
