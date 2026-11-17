// Far trees: a picture of the tree (TreeImpostors.cs atlas) on a quad that turns around its
// vertical axis to face the camera. Per instance: transform = position + uniform size,
// COLOR = tree tint, INSTANCE_CUSTOM.xy = atlas cell.
shader_type spatial;
render_mode cull_disabled, world_vertex_coords, diffuse_burley, specular_disabled;

uniform sampler2D atlas : source_color, filter_linear_mipmap;
uniform float cells = 6.0;
uniform vec3 translucency = vec3(0.16, 0.2, 0.08);

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	float size = length(MODEL_MATRIX[1].xyz);
	vec3 to_cam = CAMERA_POSITION_WORLD - origin;
	to_cam.y = 0.0;
	to_cam = normalize(to_cam);
	vec3 right = normalize(cross(vec3(0.0, 1.0, 0.0), to_cam));
	VERTEX = origin + right * UV2.x * size + vec3(0.0, UV2.y * size, 0.0);
	// Lit as a rounded volume facing the viewer, a little toward the sky.
	NORMAL = normalize(to_cam + vec3(0.0, 0.6, 0.0));
	UV = (INSTANCE_CUSTOM.xy + UV) / cells;
}

void fragment() {
	vec4 t = texture(atlas, UV);
	ALBEDO = t.rgb * COLOR.rgb;
	// Mipmaps thin the leaves out; push alpha up so far crowns stay full.
	ALPHA = clamp(t.a * 1.4, 0.0, 1.0);
	ALPHA_SCISSOR_THRESHOLD = 0.5;
	ROUGHNESS = 0.9;
	BACKLIGHT = translucency;
}
