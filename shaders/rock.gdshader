// Boulders: the same scanned rock as the terrain cliffs (Poly Haven rock_face_03, packed as
// albedo+height / normal+roughness), projected triplanar in world space so there are no UV
// seams and neighbouring boulders never show the same patch. Moss collects on the upper,
// sheltered parts and in the texture's crevices; the per-instance colour adds slate/granite variety.
shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D rock_ah : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D rock_nr : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float tile = 2.5;
uniform vec3 moss_color : source_color = vec3(0.24, 0.29, 0.12);
uniform float moss_amount = 0.55;

varying vec3 world_pos;
varying vec3 world_normal;
varying float rock_size;

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
	rock_size = length(MODEL_MATRIX[0].xyz);
}

void fragment() {
	// Planar reflection: skip underwater parts when drawn from the mirror camera below the water.
	if (CAMERA_POSITION_WORLD.y < -0.01 && world_pos.y < -0.01) discard;

	vec3 n_geo = normalize(world_normal);
	vec3 w = pow(abs(n_geo), vec3(6.0));
	w /= w.x + w.y + w.z;
	// World-space mapping: boulders at different spots show different parts of the texture.
	// Smaller stones use a finer tile, so a 30 cm stone still shows grain instead of one flat patch.
	vec3 p = world_pos / (tile * clamp(rock_size / 1.6, 0.3, 1.0));

	vec4 ah = vec4(0.0);
	vec3 n = vec3(0.0);
	float rough = 0.0;
	// u_axis / v_axis: world directions in which UV.x / UV.y increase (map +Y points along -v_axis).
	if (w.x > 0.02) {
		vec4 a = texture(rock_ah, vec2(p.z, -p.y)), nr = texture(rock_nr, vec2(p.z, -p.y));
		vec2 t = nr.xy * 2.0 - 1.0;
		ah += a * w.x; rough += nr.b * w.x;
		n += (n_geo + vec3(0, 0, 1) * t.x + vec3(0, 1, 0) * t.y) * w.x;
	}
	if (w.z > 0.02) {
		vec4 a = texture(rock_ah, vec2(p.x, -p.y)), nr = texture(rock_nr, vec2(p.x, -p.y));
		vec2 t = nr.xy * 2.0 - 1.0;
		ah += a * w.z; rough += nr.b * w.z;
		n += (n_geo + vec3(1, 0, 0) * t.x + vec3(0, 1, 0) * t.y) * w.z;
	}
	if (w.y > 0.02) {
		vec4 a = texture(rock_ah, p.xz), nr = texture(rock_nr, p.xz);
		vec2 t = nr.xy * 2.0 - 1.0;
		ah += a * w.y; rough += nr.b * w.y;
		n += (n_geo + vec3(1, 0, 0) * t.x - vec3(0, 0, 1) * t.y) * w.y;
	}
	n = normalize(n);

	// Moss: on upward faces, broken up by noise, settling first into low spots of the texture.
	float up = smoothstep(0.25, 0.85, n.y);
	float patches = vnoise(world_pos * 1.3) * 0.65 + vnoise(world_pos * 4.1) * 0.35;
	float moss = clamp(up * moss_amount * 1.6 + (patches - 0.5) * 1.2 - ah.a * 0.9 + 0.1, 0.0, 1.0);
	moss = smoothstep(0.35, 0.65, moss);

	// Per-boulder tint, softened so boulders match the cliffs around them.
	vec3 rock = ah.rgb * mix(vec3(1.0), COLOR.rgb, 0.45);
	// Tiny path stones read as softly painted, pale pebbles against the warm earth.
	float pebble = 1.0 - smoothstep(0.32, 0.55, rock_size);
	moss *= 1.0 - pebble * 0.8;
	rock = mix(rock, vec3(0.55, 0.57, 0.46) * (0.9 + ah.r * 0.3), pebble * 0.75);
	n = normalize(mix(n, n_geo, pebble * 0.8));
	ALBEDO = mix(rock, moss_color * (0.8 + 0.4 * patches), moss);
	ROUGHNESS = mix(rough, 0.95, moss);
	// Moss softens the surface detail it covers.
	NORMAL = normalize((VIEW_MATRIX * vec4(normalize(mix(n, n_geo, moss * 0.6)), 0.0)).xyz);
}
