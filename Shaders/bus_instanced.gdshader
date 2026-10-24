shader_type canvas_item;

// Hardware-instanced transit bus shader for MultiMeshInstance2D.
// Instance COLOR.rgb carries the transit route color.

varying vec4 v_route_color;

void vertex() {
	v_route_color = COLOR;
}

void fragment() {
	// UV in [0, 1]: X along bus length (0 = rear, 1 = front), Y across bus width
	vec2 p = UV * 2.0 - 1.0;

	vec2 d = abs(p) - vec2(0.90, 0.82);
	float dist = length(max(d, vec2(0.0))) + min(max(d.x, d.y), 0.0) - 0.08;
	if (dist > 0.02) {
		discard;
	}

	vec3 col = v_route_color.rgb;

	// Front panoramic windshield (x in [0.46, 0.82], |y| < 0.78)
	if (p.x > 0.46 && p.x < 0.82 && abs(p.y) < 0.78) {
		col = mix(col, vec3(0.85, 0.95, 1.0), 0.90);
	}

	// Roof HVAC / skylight detail near center (x in [-0.35, 0.25], |y| < 0.44)
	if (p.x > -0.35 && p.x < 0.25 && abs(p.y) < 0.44) {
		col = mix(col, vec3(1.0, 1.0, 1.0), 0.35);
	}

	// Front headlights
	if (p.x > 0.82 && abs(abs(p.y) - 0.64) < 0.20) {
		col = vec3(1.0, 1.0, 1.0);
	}

	// Rear taillights
	if (p.x < -0.82 && abs(abs(p.y) - 0.64) < 0.20) {
		col = vec3(0.95, 0.15, 0.15);
	}

	float alpha = 1.0 - smoothstep(-0.02, 0.02, dist);
	COLOR = vec4(col, alpha);
}
