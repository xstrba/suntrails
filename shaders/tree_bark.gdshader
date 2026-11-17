// MegaKit tree bark: the pack's painted bark with its normal map, the occlusion baked into the
// model's vertex colours, and the shared wind. INSTANCE_CUSTOM.a whitens it for birches.
shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://shaders/tree_wind.gdshaderinc"

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float wind_strength = 1.0;

varying float birch;
varying float ao;

void vertex() {
	vec3 tree_pos = MODEL_MATRIX[3].xyz;
	float w = clamp(VERTEX.y / 9.0, 0.0, 1.3);
	vec2 sway = vec2(w, tree_pos.x * 0.37 + tree_pos.z * 0.21 + (VERTEX.x + VERTEX.z) * 0.4);
	VERTEX += tree_wind(tree_pos, VERTEX, sway, TIME, wind_strength);
	birch = INSTANCE_CUSTOM.a;
	ao = COLOR.r;
}

void fragment() {
	vec3 bark = texture(albedo_tex, UV).rgb;
	// Birch: pale, slightly cool bark with the dark markings kept from the texture's shadows.
	float lum = dot(bark, vec3(0.3, 0.59, 0.11));
	vec3 white = mix(vec3(0.12, 0.11, 0.1), vec3(0.86, 0.86, 0.82), smoothstep(0.12, 0.3, lum));
	ALBEDO = mix(bark, white, birch) * mix(0.45, 1.0, ao);
	NORMAL_MAP = texture(normal_tex, UV).rgb;
	ROUGHNESS = 0.92;
}
