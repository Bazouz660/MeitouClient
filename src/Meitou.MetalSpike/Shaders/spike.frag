#version 450 core
#extension GL_EXT_nonuniform_qualifier : require

// The engine's binding pattern in miniature: a uniform block (globals), a storage buffer table
// (the bindless-ish tables the renderer indexes), a sampled texture, a texture table whose
// layer is picked at runtime, the output of a compute pass, and the bindless table itself -
// separate textures and separate samplers, indexed at runtime, the way the engine's set 1 is.
layout(location = 0) in vec2 vUv;
layout(binding = 0) uniform sampler2D uTex;
layout(binding = 3) uniform sampler2DArray uLayers;
layout(binding = 4) uniform sampler2D uComputed;

layout(set = 6, binding = 0) uniform texture2D uTable[];
layout(set = 6, binding = 1) uniform sampler uSamplers[];

layout(std140, binding = 1) uniform Globals
{
    vec4 tint;
    float time;
} u;

layout(std430, binding = 2) readonly buffer Table
{
    vec4 items[];
} table;

layout(location = 0) out vec4 oColor;

void main()
{
    // Dynamic index into the storage table - the pattern the renderer uses for its lookup tables.
    uint index = uint(clamp(vUv.x, 0.0, 1.0) * 3.999);
    vec4 base = texture(uTex, vUv);
    vec4 pick = table.items[index];
    float pulse = 0.65 + 0.35 * sin(u.time + float(index));

    // Dynamic layer of the texture table: the same shape as a bindless table, indexed at runtime.
    vec4 layer = texture(uLayers, vec3(vUv, float(index)));

    // The compute pass's output, sampled back.
    vec4 computed = texture(uComputed, vUv);

    // The bindless table: a texture and a sampler, both picked at runtime.
    vec4 entry = texture(sampler2D(uTable[nonuniformEXT(index)], uSamplers[nonuniformEXT(index)]), vUv);

    oColor = vec4(base.rgb * u.tint.rgb * pulse, 1.0) + pick * 0.25 + layer * 0.5 + computed * 0.4 + entry * 0.6;
}
