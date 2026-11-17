// Photo mode lens character, drawn over the finished frame: corners darken (vignetting, stronger
// with the aperture wide open) and high ISO adds sensor grain.
shader_type canvas_item;

uniform sampler2D screen : hint_screen_texture, filter_linear;
uniform float vignette = 0.2;
uniform float grain = 0.0;
uniform float seed = 0.0;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

void fragment() {
	vec3 c = texture(screen, SCREEN_UV).rgb;
	float falloff = 1.0 - smoothstep(0.3, 0.8, length(SCREEN_UV - 0.5)) * vignette;
	float noise = (hash12(FRAGCOORD.xy + seed * 113.7) - 0.5) * grain;
	COLOR = vec4(c * falloff * (1.0 + noise * 4.0) + noise * 0.15, 1.0);
}
