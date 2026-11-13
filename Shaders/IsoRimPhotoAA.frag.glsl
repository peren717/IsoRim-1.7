#version 450

// IsoRim anti-aliasing and sharpening of a photo (Api 76, CaptureView): the shown view's pass (IsoRimAA.glsl) over the
// photo's own picture, drawn with Graphics.Blit (the picture comes in as _MainTex, the same size as the target).

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    float _AAStrength; // @default = 1.0    0 = off, 1 = full
    float _Sharpen;    // @default = 0.0    sharpening: 0 = off, 1 = strongest
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
#define AA_SOURCE _MainTex

#include "IsoRimAA.glsl"
