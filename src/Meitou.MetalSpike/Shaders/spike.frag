#version 450 core

// A sampled texture: the part that makes the resource mapping visible. In SPIR-V this is a
// descriptor (set 0, binding 0); in MSL it becomes a texture2d + sampler pair on the fragment stage.
layout(location = 0) in vec2 vUv;
layout(binding = 0) uniform sampler2D uTex;
layout(location = 0) out vec4 oColor;

void main()
{
    oColor = texture(uTex, vUv);
}
