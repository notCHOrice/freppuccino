// Closed-cell foam brick: walls on the Voronoi borders of jittered seeds, inside a rounded skin, cut open at the
// front (cutDepth 0 = closed). Change seed for a new foam.
param float cell = 7.0;       // average cell size
param float wall = 0.5;       // wall thickness
param float jitter = 0.8;     // seed randomness (< 0.9)
param float seed = 3.0;
param float halfX = 22.0;
param float halfY = 15.0;
param float halfZ = 15.0;
param float rounding = 4.0;
param float skin = 1.2;
param float cutDepth = 15.0;

vec3 hash33(vec3 p) {
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.xxy + p.yxx) * p.zyx);
}

vec3 site(vec3 c) { return c + 0.5 + jitter * (hash33(c + seed * 7.31) - 0.5); }

float borders(vec3 q) {
    vec3 c = floor(q);
    float best = 1e5;
    vec3 near = vec3(0.0), nearCell = vec3(0.0);
    for (int i = 0; i < 27; i++) {
        vec3 g = c + vec3(float(i % 3), float((i / 3) % 3), float(i / 9)) - 1.0;
        vec3 r = site(g) - q;
        float d = dot(r, r);
        if (d < best) { best = d; near = r; nearCell = g; }
    }
    float md = 1e5;
    for (int i = 0; i < 27; i++) {
        vec3 g = nearCell + vec3(float(i % 3), float((i / 3) % 3), float(i / 9)) - 1.0;
        vec3 r = site(g) - q;
        vec3 dr = r - near;
        if (dot(dr, dr) > 1e-6) md = min(md, dot(0.5 * (near + r), normalize(dr)));
    }
    return md;
}

float sdf(vec3 p) {
    float foam = borders(p / cell) * cell - 0.5 * wall;
    float body = sdBox(p, vec3(halfX, halfY, halfZ) - rounding) - rounding;
    float shell = max(body, -(body + skin));
    float d = min(max(foam, body), shell);
    return max(d, p.z - (halfZ - cutDepth));
}
