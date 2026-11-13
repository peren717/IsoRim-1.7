#version 450

// IsoRim colour grade: every pixel of the picture drawn so far (scene grab IsoRimGrade, queue 4900)
// is looked up in a LUT strip and mixed in by _Strength. The strip has N slices of N x N side by side
// (N = its height, 32 for the LUT files): blue picks the slice, red runs across it, green up it. The
// lookup is the one of the 1.5/1.6 LUT shader: bilinear inside the two nearest blue slices, then
// mixed by blue.

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    float _Strength;    // @default = 1.0
    float _TestPattern; // @default = 0.0   harness only: grade a band of known colours instead
} u;

layout(set = 2, binding = 1) uniform sampler2D _LutTex; // @default = white
layout(set = 2, binding = 2) uniform sampler2D _GrabIsoRimGrade;
#define GRADE_SOURCE _GrabIsoRimGrade

#include "IsoRimGrade.glsl"
