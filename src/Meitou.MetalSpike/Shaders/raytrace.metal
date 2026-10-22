#include <metal_stdlib>
using namespace metal;
using namespace metal::raytracing;

// A camera-ray debug view and a DDGI-sized probe trace, both against the same primitive
// acceleration structure. This is deliberately not the game's shader path: it is the smallest
// useful probe of Metal traversal cost before porting GiScene/GiProbes.
struct Camera
{
    float4 eye;
    float4 forward;
    float4 right;
    float4 up;
    float tanHalfFov;
    uint width;
    uint height;
    uint pad4;
};

struct ProbeParams
{
    uint raysPerProbe;
    uint probeCount;
    uint phaseCount;
    uint phase;
};

kernel void rt_camera(uint2 gid [[thread_position_in_grid]],
                      constant Camera& camera [[buffer(0)]],
                      const device float* positions [[buffer(1)]],
                      const device uint* indices [[buffer(2)]],
                      primitive_acceleration_structure accel [[buffer(3)]],
                      texture2d<float, access::write> target [[texture(0)]])
{
    uint width = target.get_width();
    uint height = target.get_height();
    if (gid.x >= width || gid.y >= height) return;

    float2 uv = (float2(gid) + 0.5f) / float2(width, height);
    float2 ndc = float2(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f);
    float aspect = float(width) / float(height);

    ray r;
    r.origin = camera.eye.xyz;
    r.direction = normalize(camera.forward.xyz + camera.right.xyz * ndc.x * aspect * camera.tanHalfFov +
                            camera.up.xyz * ndc.y * camera.tanHalfFov);
    r.min_distance = 0.01f;
    r.max_distance = 20000.0f;

    intersector<triangle_data> isect;
    isect.assume_geometry_type(geometry_type::triangle);
    isect.force_opacity(forced_opacity::opaque);
    auto hit = isect.intersect(r, accel);

    float3 sky = mix(float3(0.02f, 0.04f, 0.08f), float3(0.12f, 0.22f, 0.38f), uv.y);
    float3 color = sky;
    float covered = 0.0f;
    if (hit.type == intersection_type::triangle) {
        covered = 1.0f;
        uint3 tri = uint3(indices[hit.primitive_id * 3], indices[hit.primitive_id * 3 + 1], indices[hit.primitive_id * 3 + 2]);
        float3 p0 = float3(positions[tri.x * 3], positions[tri.x * 3 + 1], positions[tri.x * 3 + 2]);
        float3 p1 = float3(positions[tri.y * 3], positions[tri.y * 3 + 1], positions[tri.y * 3 + 2]);
        float3 p2 = float3(positions[tri.z * 3], positions[tri.z * 3 + 1], positions[tri.z * 3 + 2]);
        float2 bary = hit.triangle_barycentric_coord;
        float3 hitPoint = p0 * (1.0f - bary.x - bary.y) + p1 * bary.x + p2 * bary.y;
        float3 n = normalize(cross(p1 - p0, p2 - p0));
        float3 light = normalize(float3(0.4f, 0.8f, 0.3f));
        float ndl = saturate(dot(n, light));
        float heightTint = saturate((hitPoint.y + 20.0f) / 50.0f);
        float3 albedo = mix(float3(0.14f, 0.20f, 0.11f), float3(0.52f, 0.47f, 0.35f), heightTint);
        color = albedo * (0.22f + 0.78f * ndl) + sky * 0.08f;
        color *= exp(-hit.distance * 0.00008f);
    }

    target.write(float4(color, covered), gid);
}

kernel void rt_probes(uint gid [[thread_position_in_grid]],
                      constant ProbeParams& params [[buffer(0)]],
                      const device float3* positions [[buffer(1)]],
                      const device uint* indices [[buffer(2)]],
                      primitive_acceleration_structure accel [[buffer(3)]],
                      device atomic_uint* hitCount [[buffer(4)]])
{
    uint probesThisPhase = params.probeCount / params.phaseCount;
    if (gid >= probesThisPhase * params.raysPerProbe) return;

    uint probe = gid / params.raysPerProbe;
    uint rayIndex = gid % params.raysPerProbe;
    uint absoluteProbe = probe * params.phaseCount + params.phase;
    uint side = 32;
    uint layer = 8;
    uint column = absoluteProbe % side;
    uint row = (absoluteProbe / side) % side;
    uint level = (absoluteProbe / (side * side)) % layer;

    float spacing = 128.0f;
    float3 origin = float3((float(column) - float(side) * 0.5f) * spacing,
                           40.0f + float(level) * 96.0f,
                           (float(row) - float(side) * 0.5f) * spacing);

    const float golden = 2.39996323f;
    float y = 1.0f - 2.0f * (float(rayIndex) + 0.5f) / float(params.raysPerProbe);
    float radius = sqrt(max(0.0f, 1.0f - y * y));
    float phi = float(rayIndex) * golden + float(absoluteProbe) * 0.37f;
    float3 direction = normalize(float3(cos(phi) * radius, y, sin(phi) * radius));

    ray r;
    r.origin = origin;
    r.direction = direction;
    r.min_distance = 0.05f;
    r.max_distance = 4000.0f;

    intersector<triangle_data> isect;
    isect.assume_geometry_type(geometry_type::triangle);
    isect.force_opacity(forced_opacity::opaque);
    auto hit = isect.intersect(r, accel);
    if (hit.type == intersection_type::triangle) {
        atomic_fetch_add_explicit(hitCount, 1, memory_order_relaxed);
    }
}
