#version 450

// IsoRim sky (perspective camera only): a full-screen quad at the far end of the depth range
// (reverse-Z: 0), drawn with the depth test on, so it only covers pixels nothing was drawn on.
// The ray through each corner is worked out from the camera basis set by IsoRim each frame.

layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(location = 0) out vec3 vRay;

// Must match IsoRimSky.frag.glsl and IsoRimSkyPaint.frag.glsl exactly (the layout is read from the fragment stage).
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _SkyFwd;      // @default = vec4(0.0, 0.0, 1.0, 0.0)
    vec4 _SkyRight;    // @default = vec4(1.0, 0.0, 0.0, 0.0)   right * tan(fov/2) * aspect
    vec4 _SkyUp;       // @default = vec4(0.0, 1.0, 0.0, 0.0)   up * tan(fov/2)
    vec4 _SkyZenith;   // @default = vec4(0.3, 0.5, 0.85, 1.0)
    vec4 _SkyHorizon;  // @default = vec4(0.75, 0.82, 0.9, 1.0)
    vec4 _SkySun;      // @default = vec4(0.0, 1.0, 0.0, 0.0)   toward the sun, w = how much it shows
    vec4 _SkySunColor; // @default = vec4(1.0, 0.95, 0.8, 1.0)
    vec4 _SkyMoon;     // @default = vec4(0.0, 1.0, 0.0, 0.0)   toward the moon, w = how much it shows
    vec4 _SkyCelA;     // @default = vec4(1.0, 0.0, 0.0, 0.0)   celestial sphere: x axis in world space, w = gas giant azimuth
    vec4 _SkyCelB;     // @default = vec4(0.0, 0.0, 1.0, 0.0)   y axis, w = gas giant height
    vec4 _SkyCelP;     // @default = vec4(0.0, 1.0, 0.0, 0.0)   the pole (z axis), w = how much lower the sun, moon and gas giant are drawn
    vec4 _SkyNight;    // @default = vec4(0.0, 0.0, 0.0, 0.0)   stars, Milky Way, planets, gas giant (how much each shows)
    vec4 _SkyMisc;     // @default = vec4(0.0, 0.5, 0.0, 1.0)   days passed, gas giant size, seconds (twinkle), daylight
    vec4 _SkyTrueSun;  // @default = vec4(0.0, 1.0, 0.0, 1.0)   the sun's real direction (below the horizon at night), w = moon phase
    vec4 _SkySpace;    // @default = vec4(0.0, 0.0, 0.0, 0.0)   x = space map, y = planet backdrop below the horizon, z = its rim of air
    vec4 _SkyDim;      // @default = vec4(1.0, 1.0, 1.0, 0.0)   the map's sky light colour, w = 1 in the dim pass (IsoRimSkyDim)
    vec4 _SkyMid;      // @default = vec4(0.5, 0.66, 0.88, 1.0)   painted style only (IsoRimSkyPaint.frag.glsl)
    vec4 _SkyCloudLit; // @default = vec4(1.0, 0.98, 0.92, 1.0)   painted style only
    vec4 _SkyCloudShade; // @default = vec4(0.68, 0.76, 0.92, 1.0) painted style only
    vec4 _SkyCloud;    // @default = vec4(0.0, 0.0, 0.0, 0.0)   painted style only
} u;

void main()
{
    gl_Position = vec4(inPosition.xy, 0.0, 1.0);
    // Clip y runs down the screen in this engine (its projections flip y).
    vRay = u._SkyFwd.xyz + u._SkyRight.xyz * inPosition.x - u._SkyUp.xyz * inPosition.y;
}
