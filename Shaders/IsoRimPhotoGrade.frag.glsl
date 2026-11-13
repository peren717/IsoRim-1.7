#version 450

// IsoRim colour grade of a photo (Api 76, CaptureView): the shown view's grade (IsoRimGrade.glsl) over the photo's own
// picture, drawn with Graphics.Blit (the picture comes in as _MainTex, the same size as the target).

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    float _Strength;    // @default = 1.0
    float _TestPattern; // @default = 0.0   (kept 0 for photos)
} u;

layout(set = 2, binding = 1) uniform sampler2D _LutTex; // @default = white
layout(set = 2, binding = 2) uniform sampler2D _MainTex;
#define GRADE_SOURCE _MainTex

#include "IsoRimGrade.glsl"
