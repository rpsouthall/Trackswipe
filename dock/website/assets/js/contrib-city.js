/* Anchor — "My commits, as a city"
   A real snapshot of github.com/ajaykontham's contribution graph,
   rendered as a 3D skyline. No dragging, no clicking — the camera
   is purely scroll-scrubbed: scrolling the page moves it from a
   flat, top-down read (like the real GitHub graph) into the angled
   skyline view, with buildings rising in step.

   The data in assets/data/github-contributions.json is a static
   snapshot fetched at build time from github.com/users/ajaykontham/
   contributions — GitHub's contribution calendar has no public,
   CORS-enabled read API, so it can't be fetched live from a
   browser on another origin. */
(function () {
  "use strict";

  const root = document.getElementById("city");
  if (!root || typeof THREE === "undefined") return;

  const viewport = document.getElementById("city-viewport");
  const peakDescEl = document.getElementById("city-peak-desc");
  const peakBadgeEl = document.getElementById("city-peak-badge");
  const peakAmtEl = document.getElementById("city-peak-amt");
  const peakDateEl = document.getElementById("city-peak-date");
  const statTotalEl = document.getElementById("city-stat-total");

  const GH = { 0: 0xebedf0, 1: 0x9be9a8, 2: 0x40c463, 3: 0x30a14e, 4: 0x216e39 };

  let WEEKS = 53;
  const DAYS_PER_WEEK = 7;
  let peakBuildingRef = null;
  let peakCommits = 0;

  /* ---------------------------------------------------------------------
   * 1. Scene, camera, lights — no OrbitControls, the canvas never
   *    intercepts pointer input (see pointer-events:none in CSS).
   * ------------------------------------------------------------------- */
  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(26, viewport.clientWidth / Math.max(1, viewport.clientHeight), 0.1, 1000);

  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, powerPreference: "high-performance" });
  renderer.setSize(viewport.clientWidth, viewport.clientHeight);
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;
  viewport.appendChild(renderer.domElement);

  scene.add(new THREE.AmbientLight(0xffffff, 0.74));

  const hemi = new THREE.HemisphereLight(0xffffff, 0xe6e8eb, 0.35);
  hemi.position.set(0, 40, 0);
  scene.add(hemi);

  const dirLight = new THREE.DirectionalLight(0xfffdf9, 0.66);
  dirLight.position.set(26, 48, 34);
  dirLight.castShadow = true;
  dirLight.shadow.mapSize.set(2048, 2048);
  dirLight.shadow.camera.near = 10;
  dirLight.shadow.camera.far = 130;
  const dBounds = 28;
  dirLight.shadow.camera.left = -dBounds;
  dirLight.shadow.camera.right = dBounds;
  dirLight.shadow.camera.top = dBounds;
  dirLight.shadow.camera.bottom = -dBounds;
  dirLight.shadow.bias = -0.0005;
  scene.add(dirLight);

  const fillLight = new THREE.DirectionalLight(0xeef6f0, 0.26);
  fillLight.position.set(-30, 20, -15);
  scene.add(fillLight);

  /* ---------------------------------------------------------------------
   * 2. The grid of buildings, built from the real contribution data
   * ------------------------------------------------------------------- */
  const cityGroup = new THREE.Group();
  scene.add(cityGroup);

  const SPACING_X = 0.74;
  const SPACING_Z = 0.78;
  const BAR_WIDTH = 0.58;
  const BAR_DEPTH = 0.6;
  const PEAK_HEIGHT_UNITS = 12.5; // tallest building always reads the same height, regardless of data scale

  let gridWidth = 0;
  let gridDepth = (DAYS_PER_WEEK - 1) * SPACING_Z;
  const unitBoxGeo = new THREE.BoxGeometry(BAR_WIDTH, 1, BAR_DEPTH);
  unitBoxGeo.translate(0, 0.5, 0);

  const peakRingGeo = new THREE.RingGeometry(0.42, 0.58, 32);
  peakRingGeo.rotateX(-Math.PI / 2);
  const peakRingMat = new THREE.MeshBasicMaterial({ color: 0x22c3a6, side: THREE.DoubleSide, transparent: true, opacity: 0 });
  const peakRing = new THREE.Mesh(peakRingGeo, peakRingMat);
  cityGroup.add(peakRing);

  let buildings = [];
  let basePad = null;

  function buildCity(days) {
    gridWidth = (WEEKS - 1) * SPACING_X;

    basePad = new THREE.Mesh(
      new THREE.BoxGeometry(gridWidth + 1.8, 0.18, gridDepth + 1.6),
      new THREE.MeshLambertMaterial({ color: 0xe9eae7 })
    );
    basePad.position.set(0, -0.09, 0);
    basePad.receiveShadow = true;
    cityGroup.add(basePad);

    peakCommits = days.reduce((m, d) => Math.max(m, d.commits), 1);
    const heightScale = PEAK_HEIGHT_UNITS / peakCommits;

    days.forEach((d) => {
      const x = d.week * SPACING_X - gridWidth / 2;
      const z = d.day * SPACING_Z - gridDepth / 2;
      const targetHeight = d.commits === 0 ? 0.08 : Math.max(0.28, d.commits * heightScale);

      const mesh = new THREE.Mesh(unitBoxGeo, new THREE.MeshLambertMaterial({ color: GH[d.level] || GH[0], emissive: 0x000000 }));
      mesh.position.set(x, 0, z);
      mesh.scale.set(1, 0.001, 1);
      mesh.castShadow = d.commits > 0;
      mesh.receiveShadow = true;
      mesh.userData = { week: d.week, day: d.day, commits: d.commits, targetHeight };

      if (d.commits === peakCommits && !peakBuildingRef) {
        peakBuildingRef = mesh;
        peakRing.position.set(x, 0.03, z);
      }

      cityGroup.add(mesh);
      buildings.push(mesh);
    });
  }

  /* ---------------------------------------------------------------------
   * 3. Two camera states — a near-flat overhead read, and the angled
   *    skyline — interpolated purely by scroll position.
   * ------------------------------------------------------------------- */
  function easeInOut(t) {
    return t < 0.5 ? 8 * t * t * t * t : 1 - Math.pow(-2 * t + 2, 4) / 2;
  }

  function responsiveDistanceMultiplier() {
    const aspect = Math.max(0.45, viewport.clientWidth / Math.max(1, viewport.clientHeight));
    if (aspect < 1.85) return Math.min(2.35, Math.pow(1.85 / aspect, 0.82));
    return 1;
  }

  function overheadVectors() {
    const mult = responsiveDistanceMultiplier();
    const r = 50 * mult, phi = 0.26;
    const target = new THREE.Vector3(0, 0.6, 0);
    const pos = new THREE.Vector3(target.x, target.y + r * Math.cos(phi), target.z + r * Math.sin(phi));
    return { pos, target };
  }

  function frontVectors() {
    const mult = responsiveDistanceMultiplier();
    const r = 49 * mult, theta = -0.06, phi = 1.3;
    const target = new THREE.Vector3(0, 3.1 / Math.sqrt(mult), 0);
    const pos = new THREE.Vector3(
      target.x + r * Math.sin(phi) * Math.sin(theta),
      target.y + r * Math.cos(phi),
      target.z + r * Math.sin(phi) * Math.cos(theta)
    );
    return { pos, target };
  }

  /* ---------------------------------------------------------------------
   * 4. Scroll-driven progress (0 = top-down, 1 = fully risen skyline)
   * ------------------------------------------------------------------- */
  let rawProgress = 0;
  let smoothedProgress = 0;

  function computeProgress() {
    const rect = viewport.getBoundingClientRect();
    const vh = window.innerHeight || document.documentElement.clientHeight;
    const startY = vh;        // card top at the bottom edge of the viewport → 0
    const endY = vh * 0.32;   // card top ~a third down the viewport → 1
    const t = (startY - rect.top) / (startY - endY);
    return Math.max(0, Math.min(1, t));
  }

  /* ---------------------------------------------------------------------
   * 5. Render loop
   * ------------------------------------------------------------------- */
  const projected = new THREE.Vector3();
  let booted = false;

  function updatePeakBadge(opacity) {
    peakBadgeEl.style.opacity = String(opacity);
    if (!peakBuildingRef || opacity <= 0.01) return;

    projected.set(peakBuildingRef.position.x, peakBuildingRef.scale.y + 0.35, peakBuildingRef.position.z);
    projected.project(camera);

    const cw = viewport.clientWidth, ch = viewport.clientHeight;
    let x = (projected.x * 0.5 + 0.5) * cw;
    let y = (-(projected.y * 0.5) + 0.5) * ch;
    x = Math.max(76, Math.min(cw - 76, x));
    y = Math.max(46, Math.min(ch - 38, y));

    peakBadgeEl.style.left = `${x}px`;
    peakBadgeEl.style.top = `${y}px`;
  }

  function animate() {
    requestAnimationFrame(animate);
    if (!booted) return;

    rawProgress = computeProgress();
    smoothedProgress += (rawProgress - smoothedProgress) * 0.16;
    const eased = easeInOut(smoothedProgress);

    const overhead = overheadVectors();
    const front = frontVectors();
    camera.position.lerpVectors(overhead.pos, front.pos, eased);
    const target = overhead.target.clone().lerp(front.target, eased);
    camera.lookAt(target);

    buildings.forEach((mesh) => {
      const u = mesh.userData;
      const frontToBack = (DAYS_PER_WEEK - 1 - u.day) / DAYS_PER_WEEK;
      const weekOrder = u.week / WEEKS;
      const delay = (frontToBack * 0.4 + weekOrder * 0.6) * 0.55;
      const localT = Math.max(0, Math.min(1, (smoothedProgress - delay) / 0.45));
      mesh.scale.y = Math.max(0.001, u.targetHeight * easeInOut(localT));
    });

    const badgeOpacity = Math.max(0, Math.min(1, (smoothedProgress - 0.82) / 0.16));
    peakRingMat.opacity = badgeOpacity * 0.85;
    updatePeakBadge(badgeOpacity);

    renderer.render(scene, camera);
  }

  /* ---------------------------------------------------------------------
   * 6. Resize
   * ------------------------------------------------------------------- */
  function handleResize() {
    const w = viewport.clientWidth, h = viewport.clientHeight;
    if (w === 0 || h === 0) return;
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    renderer.setSize(w, h);
  }

  window.addEventListener("resize", handleResize);
  if (window.ResizeObserver) new ResizeObserver(handleResize).observe(viewport);

  /* ---------------------------------------------------------------------
   * 7. Boot — fetch the real snapshot once the card is near the viewport
   * ------------------------------------------------------------------- */
  function boot() {
    fetch("assets/data/github-contributions.json")
      .then((r) => r.json())
      .then((data) => {
        WEEKS = data.weeks;
        buildCity(data.days);

        statTotalEl.textContent = data.total.toLocaleString();
        peakDescEl.textContent = `its biggest day was ${data.peak.commits} commits, on ${data.peak.label}.`;
        peakAmtEl.textContent = `${data.peak.commits} commits`;
        peakDateEl.textContent = data.peak.label;

        booted = true;
      })
      .catch(() => {
        peakDescEl.textContent = "activity data is temporarily unavailable.";
      });
  }

  requestAnimationFrame(animate);

  if (window.IntersectionObserver) {
    const io = new IntersectionObserver(
      (entries) => {
        if (entries[0].isIntersecting) { boot(); io.disconnect(); }
      },
      { rootMargin: "400px 0px 400px 0px" }
    );
    io.observe(root);
  } else {
    boot();
  }
})();
