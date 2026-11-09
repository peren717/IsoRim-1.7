#version 450

// IsoRim scene image (Api 66, SceneImage.cs): drawn only on a frame a picture of the map view is asked for. The engine
// copies the finished map view into _GrabIsoRimShot (scene grab IsoRimShot, queue 4990: after the colour grade, before
// the interface) and SceneImage copies it once more for the caller. This pass writes the grabbed picture back unchanged.

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    float _Unused; // @default = 0.0   (kept 0: every IsoRim screen pass has its uniform block at binding 0)
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabIsoRimShot;

void main()
{
    outColor = texelFetch(_GrabIsoRimShot, ivec2(gl_FragCoord.xy), 0);
    outColor.a += u._Unused;
}
