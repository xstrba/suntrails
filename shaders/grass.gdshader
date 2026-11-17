// Stylised grass blades, coloured like the MegaKit's painted grass: a deep green base fading to
// bright yellow-green tips, with broad painterly patches of lush green, olive, dry gold and a
// little rust across the meadow. Normals lean up so a meadow shades as one soft surface instead
// of thousands of glinting blades. World-anchored wind on the tips; blades widen with distance
// so thinned-out far clumps still cover the ground.
shader_type spatial;
render_mode cull_disabled, diffuse_lambert, specular_disabled;

uniform float lod_near = 12.0;
uniform float lod_far = 55.0;
uniform float detail_scale = 1.0;
uniform float far_width = 3.0;
uniform vec3 backlight = vec3(0.2, 0.26, 0.06);
// Palette (sRGB, from the MegaKit grass texture).
uniform vec3 base_color : source_color = vec3(0.05, 0.3, 0.03);
uniform vec3 lush_tip : source_color = vec3(0.42, 0.72, 0.1);
uniform vec3 olive_tip : source_color = vec3(0.72, 0.7, 0.12);
uniform vec3 dry_tip : source_color = vec3(0.86, 0.72, 0.28);
uniform vec3 rust_tip : source_color = vec3(0.78, 0.42, 0.12);

varying vec3 world_pos;

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

void vertex() {
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float tip = UV.y * UV.y;
	float wave = sin(TIME * 1.2 + world.x * 0.32 + world.z * 0.18) * 0.07
		+ cos(TIME * 2.4 + world.x * 0.75) * 0.018;
	float widen = smoothstep(lod_near, lod_far, length(world.xz - CAMERA_POSITION_WORLD.xz) / detail_scale) * (far_width - 1.0);
	VERTEX += vec3(wave * tip, 0.0, wave * 0.45 * tip) + vec3(UV2.x, 0.0, UV2.y) * widen;
	// Soft, up-facing shading (in model space; clumps are only rotated about Y and sheared slightly).
	NORMAL = normalize(mix(NORMAL, vec3(0.0, 1.0, 0.0), 0.75));
	world_pos = world;
}

void fragment() {
	// Broad patches (tens of metres) and smaller tufts of colour.
	float broad = vnoise(world_pos.xz * 0.035);
	float tufts = vnoise(world_pos.xz * 0.6 + 13.0);
	vec3 tip_color = mix(lush_tip, olive_tip, smoothstep(0.45, 0.8, broad));
	tip_color = mix(tip_color, dry_tip, smoothstep(0.75, 0.95, broad + tufts * 0.15) * 0.8);
	tip_color = mix(tip_color, rust_tip, smoothstep(0.88, 0.98, tufts) * 0.5);
	float t = smoothstep(0.0, 0.85, UV.y);
	ALBEDO = mix(base_color, tip_color, t) * COLOR.rgb;
	BACKLIGHT = backlight * t;
}
