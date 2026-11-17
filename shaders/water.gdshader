// Lake water: planar reflection (from Water.cs's mirror camera), refraction of the lakebed
// with depth-based light absorption, Fresnel blending between the two, layered ripples,
// a sun/moon glint and a faint shoreline foam line.
shader_type spatial;
// Unshaded: the colour is assembled here from real scene images (reflection + refraction),
// so the scene's lights must not light it a second time. Fog is still applied.
render_mode blend_mix, cull_back, depth_draw_always, unshaded, shadows_disabled;

uniform vec3 deep_color : source_color = vec3(0.03, 0.12, 0.13);
// Fraction of light lost per metre for red, green and blue: real water eats red first,
// so shallow water looks clear and deeper water turns teal.
uniform vec3 absorption = vec3(0.42, 0.11, 0.08);
uniform float scattering = 0.32;
uniform float reflection_distortion = 0.025;
uniform float refraction_distortion = 0.04;
uniform float ripple_strength = 0.35;
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform vec3 sun_color = vec3(1.0);
uniform float sun_intensity = 1.0;
// Actual scene lighting (set by DayNight.cs), used to light the foam like any lit surface.
uniform float light_energy = 1.0;
uniform vec3 ambient_light = vec3(0.5);

uniform sampler2D reflection_tex : filter_linear, repeat_disable;
// With reflections off (settings), the water reflects this sky colour instead of the mirror image.
uniform bool reflection_enabled = true;
uniform vec3 sky_fallback : source_color = vec3(0.6, 0.75, 0.9);
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;

varying vec3 world_pos;

vec4 phases(vec2 p, float t) {
	return vec4(
		p.x * 0.22 + p.y * 0.14 + t * 1.35,
		p.x * -0.17 + p.y * 0.28 + t * 1.75,
		p.x * 0.45 + p.y * -0.36 + t * 2.35,
		p.x * -0.68 + p.y * -0.52 + t * 3.1);
}

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash12(i), hash12(i + vec2(1, 0)), f.x),
		mix(hash12(i + vec2(0, 1)), hash12(i + vec2(1, 1)), f.x), f.y);
}

// Slope of two scrolling noise layers moving in different directions: wind ripples.
vec2 ripple_slope(vec2 p, float t) {
	const float e = 0.05;
	vec2 a = p * 1.3 + vec2(t * 0.35, t * 0.12);
	vec2 b = p * 2.9 + vec2(-t * 0.22, t * 0.41);
	float h0 = vnoise(a) + vnoise(b) * 0.5;
	float hx = vnoise(a + vec2(e, 0)) + vnoise(b + vec2(e, 0)) * 0.5;
	float hz = vnoise(a + vec2(0, e)) + vnoise(b + vec2(0, e)) * 0.5;
	return vec2(hx - h0, hz - h0) / e;
}

// Distance from the water surface to the opaque scene behind it, along the view ray.
float depth_behind(vec2 uv, float surface_z, mat4 inv_proj) {
	float d = texture(depth_tex, uv).r;
	vec4 v = inv_proj * vec4(uv * 2.0 - 1.0, d, 1.0);
	return surface_z - v.z / v.w;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec4 ph = phases(world_pos.xz, TIME);
	VERTEX.y += sin(ph.x) * 0.065 + sin(ph.y) * 0.045 + sin(ph.z) * 0.02;
}

void fragment() {
	// Long swell from the original sine waves, plus fine wind ripples on top.
	vec4 c = cos(phases(world_pos.xz, TIME));
	float dhx = (c.x * 0.065 * 0.22 + c.y * 0.045 * -0.17 + c.z * 0.02 * 0.45 + c.w * 0.015 * -0.68) * 1.4;
	float dhz = (c.x * 0.065 * 0.14 + c.y * 0.045 * 0.28 + c.z * 0.02 * -0.36 + c.w * 0.015 * -0.52) * 1.4;
	float cam_dist = length(CAMERA_POSITION_WORLD - world_pos);
	// Ripples fade with distance so far water doesn't shimmer.
	vec2 rs = ripple_slope(world_pos.xz, TIME) * ripple_strength * (1.0 - smoothstep(20.0, 120.0, cam_dist));
	vec3 n_world = normalize(vec3(-dhx - rs.x * 0.1, 1.0, -dhz - rs.y * 0.1));
	vec3 v_world = normalize(CAMERA_POSITION_WORLD - world_pos);

	// Refraction: the scene behind the surface, shifted by the waves. If the shifted sample
	// lands on something in front of the water, fall back to the unshifted one.
	float depth = depth_behind(SCREEN_UV, VERTEX.z, INV_PROJECTION_MATRIX);
	vec2 refr_uv = SCREEN_UV + n_world.xz * refraction_distortion * clamp(depth, 0.0, 1.0);
	float refr_depth = depth_behind(refr_uv, VERTEX.z, INV_PROJECTION_MATRIX);
	if (refr_depth < 0.0) { refr_uv = SCREEN_UV; refr_depth = depth; }
	refr_depth = max(refr_depth, 0.0);
	vec3 below = textureLod(screen_tex, refr_uv, 0.0).rgb;
	// Light from the lakebed is absorbed on the way up; deep water fills in with its own colour.
	// The light path is about twice the view distance (down to the bed and back).
	vec3 transmit = exp(-absorption * refr_depth * 2.0);
	vec3 refraction = below * transmit + deep_color * (1.0 - exp(-scattering * refr_depth));

	// Reflection from the mirror camera (u flipped, see Water.cs), shifted by the waves.
	vec2 refl_uv = vec2(1.0 - SCREEN_UV.x, SCREEN_UV.y) + n_world.xz * reflection_distortion;
	vec3 reflection = reflection_enabled
		? texture(reflection_tex, clamp(refl_uv, vec2(0.001), vec2(0.999))).rgb
		: sky_fallback * (0.85 + 0.3 * n_world.x);

	// Schlick Fresnel for water (F0 = 0.02): see-through when looking down, mirror at grazing angles.
	float fresnel = 0.02 + 0.98 * pow(1.0 - clamp(dot(n_world, v_world), 0.0, 1.0), 5.0);
	vec3 col = mix(refraction, reflection, fresnel);

	// Sun or moon glint: a tight highlight plus a broader sheen.
	vec3 r = reflect(-v_world, n_world);
	float sd = max(dot(r, normalize(sun_dir)), 0.0);
	col += sun_color * sun_intensity * (pow(sd, 900.0) * 18.0 + pow(sd, 90.0) * 0.25);

	// Faint broken foam where the water meets the shore. The water is unshaded, so the foam
	// is lit by hand with the current sun/moon and ambient light (dark at night), and it
	// fades with distance, where the thin shallow band would shrink into hard dashed lines.
	float foam = (1.0 - smoothstep(0.0, 0.35, depth))
		* smoothstep(0.35, 0.75, vnoise(world_pos.xz * 3.0 + TIME * 0.3))
		* (1.0 - smoothstep(8.0, 35.0, cam_dist));
	vec3 foam_light = sun_color * light_energy * max(normalize(sun_dir).y, 0.0) + ambient_light;
	col = mix(col, 0.75 * foam_light, foam * 0.3);

	ALBEDO = col;
	// Soft edge so the shoreline doesn't show a hard intersection line.
	ALPHA = smoothstep(0.0, 0.06, depth);
}
