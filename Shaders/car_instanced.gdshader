shader_type canvas_item;

// Hardware-instanced passenger car shader for MultiMeshInstance2D.
// Instance COLOR.rgb provides body paint color; COLOR.a < 0.75 signals brake light activation.

varying vec4 v_instance_color;

void vertex() {
	v_instance_color = COLOR;
}

void fragment() {
	// UV in [0, 1]: X along vehicle length (0 = rear, 1 = front), Y across width (0 = left, 1 = right)
	vec2 p = UV * 2.0 - 1.0;

	// Rounded rectangular body mask
	vec2 d = abs(p) - vec2(0.86, 0.78);
	float dist = length(max(d, vec2(0.0))) + min(max(d.x, d.y), 0.0) - 0.10;
	if (dist > 0.02) {
		discard;
	}

	vec3 col = v_instance_color.rgb;

	// Dark tinted front windshield (x in [0.05, 0.58], |y| < 0.72)
	if (p.x > 0.05 && p.x < 0.58 && abs(p.y) < 0.72) {
		col = mix(col, vec3(0.15, 0.20, 0.25), 0.85);
	}

	// Rear window (x in [-0.62, -0.25], |y| < 0.68)
	if (p.x > -0.62 && p.x < -0.25 && abs(p.y) < 0.68) {
		col = mix(col, vec3(0.18, 0.22, 0.28), 0.75);
	}

	// Front headlights at (+0.85, +-0.62)
	if (p.x > 0.74 && abs(abs(p.y) - 0.60) < 0.22) {
		col = vec3(1.0, 1.0, 0.96);
	}

	// Rear taillights at (-0.85, +-0.62) — brighter red when stopped (v_instance_color.a < 0.75)
	if (p.x < -0.74 && abs(abs(p.y) - 0.60) < 0.22) {
		bool braking = v_instance_color.a < 0.75;
		col = braking ? vec3(1.0, 0.12, 0.12) : vec3(0.82, 0.15, 0.15);
	}

	float alpha = 1.0 - smoothstep(-0.02, 0.02, dist);
	COLOR = vec4(col, alpha);
}
