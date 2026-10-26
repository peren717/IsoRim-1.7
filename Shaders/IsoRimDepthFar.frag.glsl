#version 450

// IsoRim depth just short of the far end (Sky.cs, space maps only): a space map's drawn sections, depth only,
// written a hair nearer than the far end (reverse-Z: 0), whatever their real distance. The sky quad, drawn a
// little nearer still on space maps, passes it; the sky's dim pass (at the far end) does not, so it never darkens
// the planet a second time where the game's light overlay already does. The terrain cells get their real depth
// over it (SectionLayer_IsoRimSpaceDepth). Vertex stage: IsoRimDepthPlane.vert.glsl.

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} u;

void main()
{
    outColor = u._Color;
    gl_FragDepth = 1e-6;
}
