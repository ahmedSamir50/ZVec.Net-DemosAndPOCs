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

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float speed = 0.05;
    float frequency = 10.0;
    float noise = sin(uv.x * frequency + u_time * speed) *
                  cos(uv.y * frequency * 0.5 - u_time * speed * 1.2);
    vec2 mouse_uv = u_mouse / u_resolution;
    float dist = distance(uv, mouse_uv);
    float glow = smoothstep(0.4, 0.0, dist) * 0.15;
    vec3 color_a = vec3(0.03, 0.08, 0.15);
    vec3 color_b = vec3(0.07, 0.12, 0.22);
    vec3 accent = vec3(0.39, 0.40, 0.95);
    vec3 final_color = mix(color_a, color_b, noise * 0.5 + 0.5);
    final_color += accent * glow;
    gl_FragColor = vec4(final_color, 1.0);
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

  function render(t) {
    if (!gl || !canvas || paused) {
      rafId = null;
      return;
    }

    syncSize();
    gl.viewport(0, 0, canvas.width, canvas.height);
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
      gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
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

      canvas.addEventListener('mousemove', (event) => {
        const rect = canvas.getBoundingClientRect();
        if (!rect.width || !rect.height) return;
        const nx = (event.clientX - rect.left) / rect.width;
        const ny = 1.0 - (event.clientY - rect.top) / rect.height;
        mouse.x = nx * canvas.width;
        mouse.y = ny * canvas.height;
      });

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
