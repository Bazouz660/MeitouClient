#version 450 core

// The engine's binding pattern in miniature: a uniform block (globals), a storage buffer table
// (the bindless-ish tables the renderer indexes), and a sampled texture. In SPIR-V these are
// descriptors in set 0; in MSL they become a constant buffer, a device buffer and a texture.
layout(location = 0) in vec2 vUv;
layout(binding = 0) uniform sampler2D uTex;

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
    oColor = vec4(base.rgb * u.tint.rgb * pulse, 1.0) + pick * 0.25;
}
