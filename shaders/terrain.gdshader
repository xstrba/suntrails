// Terrain material. Five layers, chosen by height, slope and noise, and blended by each
// layer's height map so rock pokes through soil and snow settles into cracks instead of
// fading in as a smooth gradient:
//   grass  - meadow ground (colour only; grass blades cover most of it)
//   sand   - shorelines (colour only)
//   scree  - rocky high ground and moderate slopes (Poly Haven aerial_rocks_02, top-down)
//   cliff  - steep faces (Poly Haven rock_face_03, triplanar so it never stretches)
//   snow   - peaks (Poly Haven snow_02)
// The PBR sets are packed as <albedo RGB + height A> and <normal XY + roughness B>.
// Layers with no weight at a pixel are skipped, so most pixels sample only one or two.
shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D grass_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D sand_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D cliff_ah : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D cliff_nr : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D scree_ah : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D scree_nr : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D snow_ah : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D snow_nr : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float water_level = 0.0;
// Altitude bands in metres (set from Terrain.cs so grass, trees and the shader agree).
uniform float scree_line = 22.0;
uniform float grass_line = 30.0;
uniform float snow_line = 44.0;

// Real-world size of one texture tile, in metres.
uniform float cliff_tile = 4.0;
uniform float scree_tile = 4.5;
uniform float snow_tile = 3.5;

varying vec3 world_pos;
varying vec3 world_normal;
// Low-frequency noise, evaluated per vertex and interpolated (it varies over tens of metres).
varying float macro_n;
varying float mid_n;

float hash13(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.zyx + 31.32);
	return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(
		mix(mix(hash13(i), hash13(i + vec3(1, 0, 0)), f.x),
			mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), f.x), f.y),
		mix(mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), f.x),
			mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), f.x), f.y),
		f.z);
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	macro_n = vnoise(world_pos * 0.03);
	mid_n = vnoise(world_pos * 0.17 + 17.0);
}

struct Layer {
	vec3 albedo;
	float height;
	vec3 normal; // world space
	float roughness;
};

// Tangent-space normal (OpenGL, +Y = up in the image) to world space for a planar
// projection: `u_axis`/`v_axis` are the world directions in which UV.x/UV.y increase
// (UV.y runs down the image, so the map's +Y points along -v_axis).
vec3 tangent_to_world(vec2 nxy, vec3 n_geo, vec3 u_axis, vec3 v_axis) {
	return n_geo + u_axis * nxy.x - v_axis * nxy.y;
}

vec2 unpack_normal(vec4 nr) {
	return nr.xy * 2.0 - 1.0;
}

// --- Anti-tiling --------------------------------------------------------------------
// Two techniques hide the repeat of the photo textures:
// 1. Random offsets (after Inigo Quilez's "texture repetition" #3): a slowly varying noise
//    picks which of 8 randomly shifted copies of the texture to show, cross-fading between two
//    neighbours, so the same patch never lines up in a grid. Colour and normal use the same shift.
// 2. Distance scale: far away, a second sample at ~3.7x the tile size takes over, so distant
//    slopes don't show hundreds of tiny repeats.
// Derivatives are taken once, outside any branch, and passed in (textureGrad), so mip levels
// stay correct inside the per-layer branches and across the random-offset seams.
struct Tiling {
	float k_near;  // offset-choice noise for the near scale
	float k_far;   // and for the far scale
	float far;     // 0 = near scale only, 1 = far scale only
};

const float FAR_SCALE = 0.27;

vec2 tile_offset(float i) {
	return sin(vec2(3.0, 7.0) * i);
}

void sample_notile(sampler2D ah_tex, sampler2D nr_tex, vec2 uv, vec2 ddx, vec2 ddy, float k,
		bool with_normal, out vec4 ah, out vec4 nr) {
	float l = k * 8.0;
	float i = floor(l);
	vec2 oa = tile_offset(i), ob = tile_offset(i + 1.0);
	vec4 a1 = textureGrad(ah_tex, uv + oa, ddx, ddy);
	vec4 a2 = textureGrad(ah_tex, uv + ob, ddx, ddy);
	// Bias the cross-fade by the colour difference so the seam follows texture features.
	float t = smoothstep(0.2, 0.8, fract(l) - 0.1 * dot(a1 - a2, vec4(1.0)));
	ah = mix(a1, a2, t);
	nr = vec4(0.5, 0.5, 0.85, 0.85); // flat normal, rough
	if (with_normal) nr = mix(textureGrad(nr_tex, uv + oa, ddx, ddy), textureGrad(nr_tex, uv + ob, ddx, ddy), t);
}

void sample_layer(sampler2D ah_tex, sampler2D nr_tex, vec2 uv, vec2 ddx, vec2 ddy, Tiling tl,
		out vec4 ah, out vec4 nr) {
	ah = vec4(0.0);
	nr = vec4(0.5, 0.5, 0.85, 0.85);
	if (tl.far < 0.999) sample_notile(ah_tex, nr_tex, uv, ddx, ddy, tl.k_near, true, ah, nr);
	if (tl.far > 0.001) {
		// Far scale: colour only. Normal-map detail is invisible this far away (and the near
		// sample still supplies it through the transition), which saves two lookups per layer.
		vec4 fa, fn;
		sample_notile(ah_tex, nr_tex, uv * FAR_SCALE + 0.5, ddx * FAR_SCALE, ddy * FAR_SCALE, tl.k_far, false, fa, fn);
		fn.b = nr.b; // keep the near roughness through the blend
		ah = mix(ah, fa, tl.far);
		nr = mix(nr, fn, tl.far);
	}
}

// Top-down layer (scree, snow).
Layer top_layer(sampler2D ah_tex, sampler2D nr_tex, vec3 p, vec3 dpdx, vec3 dpdy, float tile, vec3 n_geo, Tiling tl) {
	vec4 ah, nr;
	sample_layer(ah_tex, nr_tex, p.xz / tile, dpdx.xz / tile, dpdy.xz / tile, tl, ah, nr);
	Layer l;
	l.albedo = ah.rgb;
	l.height = ah.a;
	l.normal = normalize(tangent_to_world(unpack_normal(nr), n_geo, vec3(1, 0, 0), vec3(0, 0, 1)));
	l.roughness = nr.b;
	return l;
}

// Triplanar cliff: each axis-aligned projection is sampled only where it matters.
Layer cliff_layer(vec3 p, vec3 dpdx, vec3 dpdy, vec3 n_geo, Tiling tl) {
	vec3 w = pow(abs(n_geo), vec3(6.0));
	w /= w.x + w.y + w.z;
	vec4 ah = vec4(0.0);
	vec3 n = vec3(0.0);
	float r = 0.0;
	vec4 a, nr;
	if (w.x > 0.02) {
		// -y: image upright on the wall
		sample_layer(cliff_ah, cliff_nr, vec2(p.z, -p.y) / cliff_tile,
			vec2(dpdx.z, -dpdx.y) / cliff_tile, vec2(dpdy.z, -dpdy.y) / cliff_tile, tl, a, nr);
		ah += a * w.x;
		n += tangent_to_world(unpack_normal(nr), n_geo, vec3(0, 0, 1), vec3(0, -1, 0)) * w.x;
		r += nr.b * w.x;
	}
	if (w.z > 0.02) {
		sample_layer(cliff_ah, cliff_nr, vec2(p.x, -p.y) / cliff_tile,
			vec2(dpdx.x, -dpdx.y) / cliff_tile, vec2(dpdy.x, -dpdy.y) / cliff_tile, tl, a, nr);
		ah += a * w.z;
		n += tangent_to_world(unpack_normal(nr), n_geo, vec3(1, 0, 0), vec3(0, -1, 0)) * w.z;
		r += nr.b * w.z;
	}
	if (w.y > 0.02) {
		sample_layer(cliff_ah, cliff_nr, p.xz / cliff_tile, dpdx.xz / cliff_tile, dpdy.xz / cliff_tile, tl, a, nr);
		ah += a * w.y;
		n += tangent_to_world(unpack_normal(nr), n_geo, vec3(1, 0, 0), vec3(0, 0, 1)) * w.y;
		r += nr.b * w.y;
	}
	Layer l;
	l.albedo = ah.rgb;
	l.height = ah.a;
	l.normal = normalize(n);
	l.roughness = r;
	return l;
}

// Blend `top` over `base` by mask, letting the layers' heights decide the exact edge.
Layer height_blend(Layer base, Layer top, float mask) {
	float t = clamp((mask - 0.5) * 2.5 + (top.height - base.height) * 1.5 + 0.5, 0.0, 1.0);
	base.albedo = mix(base.albedo, top.albedo, t);
	base.height = mix(base.height, top.height, t);
	base.normal = normalize(mix(base.normal, top.normal, t));
	base.roughness = mix(base.roughness, top.roughness, t);
	return base;
}

void fragment() {

	vec3 n_geo = normalize(world_normal);
	vec3 dpdx = dFdx(world_pos);
	vec3 dpdy = dFdy(world_pos);
	Tiling tl;
	tl.k_near = vnoise(world_pos * 0.05);
	tl.k_far = vnoise(world_pos * 0.013 + 9.1);
	tl.far = smoothstep(30.0, 80.0, length(CAMERA_POSITION_WORLD - world_pos));

	// Planar reflection: the mirror camera sits below the water, so skip anything underwater
	// or the lakebed would hide the reflected world. (The player's camera never goes under.)
	if (CAMERA_POSITION_WORLD.y < water_level - 0.01 && world_pos.y < water_level - 0.01) discard;
	float slope = 1.0 - max(n_geo.y, 0.0);
	// Noise-jittered height so layer borders don't follow contour lines.
	float h = world_pos.y + (macro_n - 0.5) * 5.0 + (mid_n - 0.5) * 1.5;

	float sand_mask = 1.0 - smoothstep(water_level + 0.3, water_level + 2.0, h);
	float scree_mask = max(smoothstep(scree_line - 3.0, scree_line + 4.0, h), smoothstep(0.1, 0.24, slope) * 0.85);
	scree_mask *= smoothstep(0.2, 0.6, macro_n + 0.25); // grassy gaps on lower slopes
	scree_mask = max(scree_mask, smoothstep(grass_line - 2.0, grass_line + 2.0, h)); // bare above the grass line
	// From ~35 degrees (loose scree's natural limit) the ground is solid cliff. The cliff texture is
	// projected from the sides, so it doesn't stretch the way top-down scree would on steep faces.
	float cliff_mask = smoothstep(0.17, 0.3, slope);
	float snow_mask = smoothstep(snow_line, snow_line + 6.0, h) * (1.0 - smoothstep(0.3, 0.5, slope));

	// Grass ground: two rotated lookups at different scales break up its tiling.
	vec2 wuv = world_pos.xz * 0.24;
	vec3 grass = mix(texture(grass_tex, wuv).rgb, texture(grass_tex, vec2(-wuv.y, wuv.x) * 0.37 + 0.31).rgb,
		smoothstep(0.25, 0.75, mid_n) * 0.6);
	grass *= mix(vec3(0.72, 0.8, 0.62), vec3(1.08, 1.0, 0.82), macro_n);
	// Stylised world: lean the photo toward a flat painted green that matches the grass blades
	// (linear colours), keeping a little of the photo's detail.
	vec3 painted = mix(vec3(0.045, 0.13, 0.02), vec3(0.15, 0.25, 0.03), smoothstep(0.3, 0.8, macro_n));
	grass = mix(grass, painted * (0.85 + 0.3 * dot(grass, vec3(0.6))), 0.65);
	Layer ground;
	ground.albedo = grass;
	ground.height = 0.35;
	ground.normal = n_geo;
	ground.roughness = 0.92;

	if (sand_mask > 0.001) {
		Layer sand;
		sand.albedo = texture(sand_tex, wuv * 1.2).rgb * mix(vec3(0.75, 0.7, 0.6), vec3(1.0, 0.96, 0.88), mid_n);
		sand.height = 0.4;
		sand.normal = n_geo;
		sand.roughness = 0.85;
		ground = height_blend(ground, sand, sand_mask);
	}
	if (scree_mask > 0.001) ground = height_blend(ground, top_layer(scree_ah, scree_nr, world_pos, dpdx, dpdy, scree_tile, n_geo, tl), scree_mask);
	if (cliff_mask > 0.001) ground = height_blend(ground, cliff_layer(world_pos, dpdx, dpdy, n_geo, tl), cliff_mask);
	if (snow_mask > 0.001) ground = height_blend(ground, top_layer(snow_ah, snow_nr, world_pos, dpdx, dpdy, snow_tile, n_geo, tl), snow_mask);

	// Broad brightness variation over tens of metres hides any remaining repetition.
	vec3 col = ground.albedo * mix(0.82, 1.12, macro_n);

	float wet = 1.0 - smoothstep(water_level + 0.05, water_level + 0.85, world_pos.y);
	ALBEDO = col * mix(1.0, 0.58, wet);
	ROUGHNESS = mix(ground.roughness, 0.4, wet);
	NORMAL = normalize((VIEW_MATRIX * vec4(ground.normal, 0.0)).xyz);
}
