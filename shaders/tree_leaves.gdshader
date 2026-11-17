// MegaKit tree leaves: the pack's white leaf textures tinted per tree (INSTANCE_CUSTOM.rgb),
// with soft wind, light shining through from behind, and gentle shading toward the crown's
// underside so crowns read as volumes. Alpha-cutout, both sides drawn.
shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

#include "res://shaders/tree_wind.gdshaderinc"

uniform sampler2D leaf_tex : source_color, filter_linear_mipmap_anisotropic;
uniform float alpha_cut = 0.5;
uniform float wind_strength = 1.0;
uniform vec3 translucency = vec3(0.35, 0.38, 0.18);

varying vec3 tint;
varying float cam_dist;
varying float under;

void vertex() {
	vec3 tree_pos = MODEL_MATRIX[3].xyz;
	// Sway grows with height (the models are 6-17 units tall); the phase varies per tree and region of the crown.
	float w = clamp(VERTEX.y / 9.0, 0.0, 1.3);
	vec2 sway = vec2(w, tree_pos.x * 0.37 + tree_pos.z * 0.21 + (VERTEX.x + VERTEX.z) * 0.4);
	VERTEX += tree_wind(tree_pos, VERTEX, sway, TIME, wind_strength);
	VERTEX += NORMAL * tree_flutter(VERTEX, sway, TIME) * 0.5;
	tint = INSTANCE_CUSTOM.rgb;
	cam_dist = length((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz - CAMERA_POSITION_WORLD);
	under = clamp(-NORMAL.y, 0.0, 1.0);
}

void fragment() {
	vec4 t = texture(leaf_tex, UV);
	// Mipmaps thin out leaf edges; push alpha up with distance so crowns stay full.
	ALPHA = t.a * mix(1.0, 1.6, smoothstep(10.0, 70.0, cam_dist));
	ALPHA_SCISSOR_THRESHOLD = alpha_cut;
	// Undersides of the crown a touch darker and cooler.
	vec3 c = t.rgb * tint * mix(vec3(1.0), vec3(0.72, 0.78, 0.8), under * 0.6);
	ALBEDO = c;
	ROUGHNESS = 0.85;
	SPECULAR = 0.2;
	BACKLIGHT = translucency * tint;
}
