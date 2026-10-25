#version 450 core

// Stage 0 of the spike used hand-written MSL. This is the shape the engine's own shaders have:
// GLSL in, positions from the vertex id (no vertex buffer yet), a varying to the fragment stage.
layout(location = 0) out vec2 vUv;

const vec2 positions[3] = vec2[3](
    vec2(-0.85, -0.85),
    vec2( 0.85, -0.85),
    vec2( 0.00,  0.85));

const vec2 uvs[3] = vec2[3](
    vec2(0.0, 0.0),
    vec2(1.0, 0.0),
    vec2(0.5, 1.0));

void main()
{
    vUv = uvs[gl_VertexIndex];
    gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0);
}
