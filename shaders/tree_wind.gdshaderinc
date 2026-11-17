// Shared tree wind for bark and leaves, so leaves stay attached to their swaying branches.
// UV2.x = sway weight (0 at the trunk base, ~1 at twig tips), UV2.y = per-branch phase.
// Returns a local-space offset. Each tree sways as a whole (a slow gust that travels across the
// landscape), branches add their own out-of-phase motion, and twig tips flutter.
vec3 tree_wind(vec3 tree_pos, vec3 local, vec2 sway, float time, float strength) {
	float w = sway.x;
	float gust = sin(time * 0.35 + tree_pos.x * 0.013 + tree_pos.z * 0.009) * 0.5 + 0.5;
	float amount = (0.25 + 0.75 * gust) * strength;
	// Whole-tree bend, mostly downwind (+x/+z), growing with height.
	vec2 bend = vec2(0.9, 0.5) * (sin(time * 0.8 + tree_pos.x * 0.07) * 0.5 + 0.6);
	vec3 offset = vec3(bend.x, 0.0, bend.y) * w * w * 0.35 * amount;
	// Branch motion, each branch with its own phase.
	float b = sin(time * 1.7 + sway.y * 2.3) * 0.5 + sin(time * 2.9 + sway.y * 4.1) * 0.25;
	offset += vec3(b, b * 0.3, -b * 0.6) * w * w * 0.12 * amount;
	return offset;
}

float tree_flutter(vec3 local, vec2 sway, float time) {
	return sin(time * 7.0 + sway.y * 9.0 + local.y * 3.0) * 0.03 * sway.x;
}
