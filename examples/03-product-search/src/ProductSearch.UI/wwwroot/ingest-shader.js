window.productSearchIngestShader = (function () {
  let rafId = null;
  let gl = null;
  let canvas = null;
  let paused = false;

  const vs = `attribute vec2 a_position;
varying vec2 v_texCoord;
void main() {
  v_texCoord = a_position * 0.5 + 0.5;
  gl_Position = vec4(a_position, 0.0, 1.0);
}`;

  const fs = `precision highp float;
uniform float u_time;
uniform vec2 u_resolution;
uniform vec2 u_mouse;
varying vec2 v_texCoord;

float hash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

void main() {
    vec2 uv = v_texCoord;
    vec2 centered_uv = (v_texCoord - 0.5) * 2.0;
    centered_uv.x *= u_resolution.x / u_resolution.y;

    vec3 glow = vec3(0.39, 0.62, 1.0);
    float intensity = 0.0;

    float grid = sin(centered_uv.x * 10.0 + u_time * 0.2) * sin(centered_uv.y * 10.0 + u_time * 0.2);
    intensity += smoothstep(0.98, 1.0, grid) * 0.22;

    for(int i = 0; i < 12; i++) {
        float t = u_time * 0.3 + float(i) * 524.5;
        vec2 pos = vec2(sin(t * 0.7), cos(t * 0.4)) * 0.8;
        float dist = length(centered_uv - pos);
        float pulse = 0.5 + 0.5 * sin(u_time * 2.0 + float(i));

        intensity += (0.012 / max(dist, 0.002)) * pulse * 0.35;

        for(int j = 0; j < 3; j++) {
            float t2 = u_time * 0.3 + float(i + j) * 123.4;
            vec2 pos2 = vec2(sin(t2 * 0.5), cos(t2 * 0.8)) * 0.8;
            float line_dist = length(centered_uv - mix(pos, pos2, clamp(dot(centered_uv-pos, pos2-pos)/dot(pos2-pos, pos2-pos), 0.0, 1.0)));
            intensity += (0.0008 / max(line_dist, 0.001)) * 0.12;
        }
    }

    float vignette = clamp(1.0 - length(uv - 0.5) * 1.5, 0.0, 1.0);
    float alpha = clamp(intensity * vignette, 0.0, 0.7);

    gl_FragColor = vec4(glow, alpha);
}`;

  function compileShader(type, src) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, src);
    gl.compileShader(shader);
    return shader;
  }

  function syncSize() {
    if (!canvas) return;
    const w = canvas.clientWidth || 1280;
    const h = canvas.clientHeight || 720;
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w;
      canvas.height = h;
    }
  }

  function onVisibilityChange() {
    paused = document.hidden || window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (!paused && rafId === null) {
      rafId = requestAnimationFrame(render);
    }
  }

  let mouse = { x: 640, y: 360 };
  let uTime = null;
  let uRes = null;
  let uMouse = null;

  function onMouseMove(event) {
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    if (!rect.width || !rect.height) return;
    const nx = (event.clientX - rect.left) / rect.width;
    const ny = 1.0 - (event.clientY - rect.top) / rect.height;
    mouse.x = nx * canvas.width;
    mouse.y = ny * canvas.height;
  }

  function render(t) {
    if (!gl || !canvas || paused) {
      rafId = null;
      return;
    }

    syncSize();
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    if (uTime) gl.uniform1f(uTime, t * 0.001);
    if (uRes) gl.uniform2f(uRes, canvas.width, canvas.height);
    if (uMouse) gl.uniform2f(uMouse, mouse.x, mouse.y);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    rafId = requestAnimationFrame(render);
  }

  return {
    init: function (element) {
      if (!element || gl) return;

      canvas = element;
      const opts = { alpha: true, premultipliedAlpha: false, antialias: false };
      gl = canvas.getContext('webgl', opts) || canvas.getContext('experimental-webgl', opts);
      if (!gl) return;

      const program = gl.createProgram();
      gl.attachShader(program, compileShader(gl.VERTEX_SHADER, vs));
      gl.attachShader(program, compileShader(gl.FRAGMENT_SHADER, fs));
      gl.linkProgram(program);
      gl.useProgram(program);

      const buffer = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
      const pos = gl.getAttribLocation(program, 'a_position');
      gl.enableVertexAttribArray(pos);
      gl.vertexAttribPointer(pos, 2, gl.FLOAT, false, 0, 0);

      uTime = gl.getUniformLocation(program, 'u_time');
      uRes = gl.getUniformLocation(program, 'u_resolution');
      uMouse = gl.getUniformLocation(program, 'u_mouse');

      if (typeof ResizeObserver !== 'undefined') {
        new ResizeObserver(syncSize).observe(canvas);
      }
      syncSize();

      window.addEventListener('mousemove', onMouseMove);

      document.addEventListener('visibilitychange', onVisibilityChange);
      onVisibilityChange();
      if (!paused) {
        rafId = requestAnimationFrame(render);
      }
    },

    dispose: function () {
      paused = true;
      if (rafId !== null) {
        cancelAnimationFrame(rafId);
        rafId = null;
      }
      window.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('visibilitychange', onVisibilityChange);
      gl = null;
      canvas = null;
    },

    scrollTerminal: function (element) {
      if (element) {
        element.scrollTop = element.scrollHeight;
      }
    }
  };
})();
