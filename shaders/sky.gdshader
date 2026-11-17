// Day/night sky (port of src/world/skyAndLighting.js + clouds.js), drawn as one sky shader.
// Godot also renders this into the radiance map, so ambient light and water reflections
// follow the time of day and include the clouds. All values are driven by DayNight.cs.
shader_type sky;

uniform vec3 zenith_color : source_color = vec3(0.12, 0.39, 0.78);
uniform vec3 horizon_color : source_color = vec3(0.64, 0.84, 0.98);
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform vec3 sun_color = vec3(1.0, 0.96, 0.92);
uniform float sun_visibility = 1.0;
uniform float night = 0.0;          // 0 = day, 1 = full night (stars, moon glow)
uniform float star_rotation = 0.0;
uniform vec3 cloud_color : source_color = vec3(0.97, 0.98, 0.99);
uniform float cloud_opacity = 0.78;
uniform float cloud_time = 0.0;
uniform float cloud_height = 350.0;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float hash13(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.zyx + 31.32);
	return fract((p.x + p.y) * p.z);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash12(i), hash12(i + vec2(1, 0)), f.x),
		mix(hash12(i + vec2(0, 1)), hash12(i + vec2(1, 1)), f.x), f.y);
}

// The web version's layered waves give the overall cumulus pattern; value-noise
// detail breaks up their regularity so edges look torn rather than wavy.
float cloud_density(vec2 xz) {
	vec2 p = xz * (0.011 * 90.0 / cloud_height) + cloud_time * 0.018;
	float n1 = sin(p.x + cos(p.y * 0.85)) * 0.5 + cos(p.y * 1.15 - sin(p.x * 0.75)) * 0.5;
	vec2 p2 = p * 2.15 + 3.7;
	float n2 = sin(p2.x * 1.1 - p2.y * 0.9) * 0.5 + cos(p2.y * 1.25 + p2.x * 0.65) * 0.5;
	vec2 p3 = p * 4.3 - 1.9;
	float n3 = sin(p3.x + p3.y * 1.3) * 0.5 + cos(p3.y - p3.x * 1.1) * 0.5;
	float waves = n1 * 0.55 + n2 * 0.3 + n3 * 0.15;
	vec2 q = p * 3.0 + cloud_time * 0.01;
	float detail = vnoise(q) * 0.5 + vnoise(q * 2.03 + 7.1) * 0.3 + vnoise(q * 4.1 - 3.3) * 0.2;
	return smoothstep(0.14, 0.72, waves * 0.75 + (detail - 0.5) * 0.7);
}

// Stars on a grid of direction cells; each cell may hold one jittered point.
vec3 stars(vec3 d) {
	float c = cos(star_rotation), s = sin(star_rotation);
	d = vec3(c * d.x - s * d.z, d.y, s * d.x + c * d.z);
	vec3 p = d * 220.0;
	vec3 cell = floor(p);
	float h = hash13(cell);
	if (h < 0.985) return vec3(0.0);
	vec3 center = cell + 0.5 + (vec3(hash13(cell + 1.7), hash13(cell + 4.1), hash13(cell + 9.3)) - 0.5) * 0.6;
	float dist = length(p - center);
	float bright = (h - 0.985) / 0.015;
	float twinkle = 0.75 + 0.25 * sin(cloud_time * 40.0 * hash13(cell + 2.2) + h * 60.0);
	vec3 tint = mix(vec3(0.75, 0.85, 1.0), vec3(1.0, 0.9, 0.78), hash13(cell + 5.5));
	return tint * smoothstep(0.32, 0.0, dist) * (0.5 + 2.5 * bright * bright) * twinkle;
}

void sky() {
	vec3 d = EYEDIR;
	vec3 moon_dir = -sun_dir;
	float up = clamp(d.y, 0.0, 1.0);

	// Gradient, then a darker ground below the horizon.
	vec3 col = mix(horizon_color, zenith_color, pow(up, 0.55));
	col = mix(col, horizon_color * 0.35, smoothstep(0.0, -0.35, d.y));

	// Sun: warm scattering around it (stronger near the horizon), then a bright disc that blooms.
	float sd = dot(d, sun_dir);
	float horizon_sun = 1.0 - smoothstep(0.0, 0.5, sun_dir.y);
	col += sun_color * (pow(max(sd, 0.0), 12.0) * (0.12 + 0.45 * horizon_sun)
		+ pow(max(sd, 0.0), 300.0) * 0.6) * sun_visibility;
	// Sun and moon are drawn ~2x real size (about 1 degree across), as games usually do.
	float sun_disc = smoothstep(0.99990, 0.99993, sd);

	// Moon: cratered disc plus a cool halo, fading in at night.
	float md = dot(d, moon_dir);
	float moon_vis = smoothstep(-0.12, 0.05, moon_dir.y);
	col += vec3(0.35, 0.5, 0.9) * pow(max(md, 0.0), 250.0) * 0.5 * night * moon_vis;
	float moon_disc = smoothstep(0.99983, 0.99986, md) * moon_vis;
	vec3 moon_col = vec3(0.0);
	if (moon_disc > 0.0) {
		vec3 t1 = normalize(cross(moon_dir, vec3(0.0, 1.0, 0.0001)));
		vec3 t2 = cross(t1, moon_dir);
		vec2 uv = vec2(dot(d, t1), dot(d, t2)) / 0.0175;
		float maria = vnoise(uv * 2.0 + 3.0) * 0.6 + vnoise(uv * 5.0) * 0.4;
		moon_col = vec3(0.86, 0.91, 1.0) * (1.05 - smoothstep(0.45, 0.8, maria) * 0.35) * 1.6;
	}

	// Celestial bodies and stars sit behind the clouds.
	vec3 behind = col;
	behind += stars(d) * night * smoothstep(0.0, 0.15, d.y);
	behind = mix(behind, sun_color * 30.0, sun_disc * sun_visibility);
	behind = mix(behind, moon_col, moon_disc);

	// Clouds: a flat deck at cloud_height, intersected along the view ray.
	if (d.y > 0.0) {
		float t = (cloud_height - POSITION.y) / d.y;
		vec2 hit = POSITION.xz + d.xz * t;
		float density = cloud_density(hit);
		// Self-shadowing: if the deck is denser toward the sun, this point is in shade.
		float toward_sun = cloud_density(hit + normalize(sun_dir.xz + 1e-4) * 40.0);
		float shade = mix(1.0, 0.68, density) * (1.0 - clamp(toward_sun - density, 0.0, 1.0) * 0.35);
		// Thin cloud edges light up when looking toward the sun (silver lining).
		float lining = pow(max(sd, 0.0), 6.0) * (1.0 - density) * 0.8 * sun_visibility;
		vec3 cloud = cloud_color * shade + sun_color * lining;
		// Fade out toward the horizon: distant clouds dissolve into haze.
		float fade = smoothstep(0.0, 0.12, d.y) * (1.0 - smoothstep(2500.0, 7000.0, t));
		behind = mix(behind, mix(cloud, horizon_color, 1.0 - smoothstep(0.0, 0.3, d.y)),
			clamp(density * cloud_opacity * fade, 0.0, 1.0));
	}
	COLOR = behind;
}
