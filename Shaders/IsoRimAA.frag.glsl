#version 450

// IsoRim anti-aliasing (FXAA, after Timothy Lottes' simple version): the engine runs without MSAA,
// so jagged edges are softened in one full-screen pass over the picture drawn so far (scene grab
// IsoRimAA, queue 4890, before the colour grade). Only pixels on a contrast edge are changed: the
// edge direction is found from the luma of the four diagonal neighbours and the pixel is blended
// along it. The grab is read with texelFetch and filtered by hand, as its sampler state is unknown.
// The same pass also sharpens (Graphics tab option, _Sharpen > 0), with or without the FXAA part.

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    float _AAStrength; // @default = 1.0    0 = off, 1 = full
    float _Sharpen;    // @default = 0.0    sharpening (contrast-adaptive, after AMD CAS): 0 = off, 1 = strongest
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabIsoRimAA;
#define AA_SOURCE _GrabIsoRimAA

#include "IsoRimAA.glsl"
