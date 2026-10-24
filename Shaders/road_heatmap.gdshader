shader_type canvas_item;

// Vertex-fragment road congestion heatmap shader.
// Vertex COLOR.r encodes normalized congestion ratio R_congestion * 0.5 (range [0.0, 2.0] -> [0.0, 1.0]).
// UV.y encodes cross-road coordinate [0.0, 1.0] for anti-aliased ribbon edges.

varying float v_congestion_ratio;

vec3 evaluate_heatmap_spectrum(float ratio) {
	vec3 free_flow = vec3(0.15, 0.85, 0.25);
	vec3 moderate  = vec3(0.95, 0.85, 0.15);
	vec3 heavy     = vec3(0.95, 0.50, 0.10);
	vec3 congested = vec3(0.95, 0.15, 0.15);
	vec3 gridlock  = vec3(0.65, 0.05, 0.40);

	if (ratio <= 0.35) {
		return free_flow;
	} else if (ratio <= 0.65) {
		return mix(free_flow, moderate, (ratio - 0.35) / 0.30);
	} else if (ratio <= 0.90) {
		return mix(moderate, heavy, (ratio - 0.65) / 0.25);
	} else if (ratio <= 1.15) {
		return mix(heavy, congested, (ratio - 0.90) / 0.25);
	} else {
		return mix(congested, gridlock, clamp((ratio - 1.15) / 0.50, 0.0, 1.0));
	}
}

void vertex() {
	// Decode R_congestion from vertex color channel R (packed as ratio * 0.5)
	v_congestion_ratio = COLOR.r * 2.0;
}

void fragment() {
	vec3 heat_col = evaluate_heatmap_spectrum(v_congestion_ratio);

	// Smooth anti-aliased edge feathering across ribbon width
	float edge_dist = abs(UV.y * 2.0 - 1.0);
	float alpha = 1.0 - smoothstep(0.82, 1.0, edge_dist);

	COLOR = vec4(heat_col, alpha * 0.95);
}
