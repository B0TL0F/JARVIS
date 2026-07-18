import { AfterViewInit, Component, ElementRef, Input, OnChanges, OnDestroy, SimpleChanges, ViewChild } from '@angular/core';
import * as THREE from 'three';

// Cinematic Iron-Man/JARVIS HUD globe. Layered spinning wireframe sphere
// with continuous animated data-arcs bouncing between random beacon points,
// pulsing shockwave rings at each beacon, orbiting satellite sprites, and a
// counter-rotating outer reticle. Container-sized via ResizeObserver so it
// scales to whatever the parent grid tile gives it.
@Component({
  selector: 'app-hud-globe',
  standalone: true,
  template: `<canvas #canvas class="globe-canvas" aria-hidden="true"></canvas>`,
  styleUrl: './hud-globe.component.css'
})
export class HudGlobeComponent implements AfterViewInit, OnChanges, OnDestroy {
  @ViewChild('canvas', { static: true }) canvasRef!: ElementRef<HTMLCanvasElement>;

  // Fleet status drives the globe's mood: idle red rotation on "up", faster
  // white pulsing on degraded, angry saturated red on "down".
  @Input() status: 'up' | 'degraded' | 'down' | 'unknown' = 'unknown';

  // Voice UI state. `listening` = wake word ("Hey Jarvis") was heard and Jarvis is capturing
  // a command — switches the globe to a steady cyan glow. `voiceLevel` (0..1, from live mic
  // RMS) drives an additional "beat" scale pulse on top of that glow while actual speech is
  // detected; at ~0 (listening but silent) the globe only glows, it does not beat.
  @Input() listening = false;
  @Input() voiceLevel = 0;

  private renderer?: THREE.WebGLRenderer;
  private scene?: THREE.Scene;
  private camera?: THREE.PerspectiveCamera;
  private globe?: THREE.Group;
  private outerGroup?: THREE.Group;
  private wire?: THREE.LineSegments;
  private wireMaterial?: THREE.LineBasicMaterial;
  private points?: THREE.Points;
  private ring?: THREE.Mesh;
  private reticle?: THREE.LineSegments;
  private resizeObserver?: ResizeObserver;
  private animFrameId?: number;

  private clock = new THREE.Clock();

  private speed = 0.0022;
  private readonly baseSpeed = 0.0022;
  private targetOpacity = 0.55;
  // The globe's "resting" color for the current fleet status (red/white-blend), kept separate
  // from `wireMaterial.color` — the cyan listening lerp below blends FROM this every frame
  // rather than mutating the live color in place, so it always eases back to red once listening
  // stops instead of getting stuck on cyan forever.
  private statusColor = new THREE.Color(0xff2b2b);

  // Smoothed copies of the voice inputs so glow/beat transitions don't snap frame-to-frame.
  private smoothedListening = 0; // 0..1, eases toward `listening ? 1 : 0`
  private smoothedVoiceLevel = 0; // 0..1, eases toward `voiceLevel`

  private readonly RED = new THREE.Color(0xff2b2b);
  private readonly WHITE = new THREE.Color(0xffffff);
  private readonly CYAN = new THREE.Color(0x00e5ff);

  // Fixed beacon anchors on the sphere surface (in spherical coords).
  private beacons: {
    point: THREE.Vector3;
    ring: THREE.Mesh;
    dot: THREE.Mesh;
    phase: number;
  }[] = [];

  // Data arcs between random beacon pairs.
  private arcs: {
    line: THREE.Line;
    material: THREE.LineBasicMaterial;
    head: THREE.Mesh;
    curve: THREE.QuadraticBezierCurve3;
    life: number;
    ttl: number;
  }[] = [];

  // Orbiting satellites.
  private satellites: {
    mesh: THREE.Mesh;
    trail: THREE.Line;
    orbit: number;
    tilt: THREE.Euler;
    speed: number;
    offset: number;
  }[] = [];

  ngAfterViewInit(): void {
    try {
      this.init();
      this.resizeObserver = new ResizeObserver(() => this.resize());
      this.resizeObserver.observe(this.canvasRef.nativeElement.parentElement!);
      this.applyStatus();
    } catch (err) {
      console.warn('HUD globe unavailable (no WebGL), falling back to static panel.', err);
    }
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['status'] && this.wire) this.applyStatus();
  }

  ngOnDestroy(): void {
    if (this.animFrameId) cancelAnimationFrame(this.animFrameId);
    this.resizeObserver?.disconnect();
    this.renderer?.dispose();
  }

  private init(): void {
    const canvas = this.canvasRef.nativeElement;
    const parent = canvas.parentElement!;
    const w = parent.clientWidth || 320;
    const h = parent.clientHeight || 320;

    this.scene = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(45, w / h, 0.1, 100);
    this.camera.position.z = 3.2;

    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(w, h, false);

    this.globe = new THREE.Group();
    this.outerGroup = new THREE.Group();

    // 1. Solid dark core — occludes the back half of the wireframe.
    const core = new THREE.Mesh(
      new THREE.SphereGeometry(0.98, 48, 48),
      new THREE.MeshBasicMaterial({ color: 0x0a0608 })
    );
    this.globe.add(core);

    // 2. Wireframe shell — main globe personality.
    this.wireMaterial = new THREE.LineBasicMaterial({ color: this.RED, transparent: true, opacity: 0.55 });
    this.wire = new THREE.LineSegments(
      new THREE.WireframeGeometry(new THREE.SphereGeometry(1, 32, 22)),
      this.wireMaterial
    );
    this.globe.add(this.wire);

    // 3. Point cloud on the sphere surface — sparse white dots.
    this.points = new THREE.Points(
      new THREE.SphereGeometry(1.01, 44, 32),
      new THREE.PointsMaterial({ color: this.WHITE, size: 0.014, transparent: true, opacity: 0.85 })
    );
    this.globe.add(this.points);

    // 4. Equator ring hugging the sphere.
    this.ring = new THREE.Mesh(
      new THREE.RingGeometry(1.18, 1.2, 96),
      new THREE.MeshBasicMaterial({ color: this.RED, transparent: true, opacity: 0.6, side: THREE.DoubleSide })
    );
    this.ring.rotation.x = Math.PI / 2.2;
    this.globe.add(this.ring);

    // 5. Beacons — six fixed anchors on the sphere. Each gets a small dot
    //    and a shockwave ring that pulses outward.
    const anchors: [number, number][] = [
      [0.4, 1.2],
      [1.1, 2.6],
      [-0.6, -1.9],
      [0.9, -0.3],
      [-1.2, 0.7],
      [0.2, 3.6]
    ];
    for (const [lat, lon] of anchors) {
      const p = this.sphericalTo(lat, lon, 1.005);
      const dotGeo = new THREE.SphereGeometry(0.028, 12, 12);
      const dotMat = new THREE.MeshBasicMaterial({ color: this.WHITE });
      const dot = new THREE.Mesh(dotGeo, dotMat);
      dot.position.copy(p);
      this.globe.add(dot);

      const ringGeo = new THREE.RingGeometry(0.03, 0.04, 32);
      const ringMat = new THREE.MeshBasicMaterial({
        color: this.RED,
        transparent: true,
        opacity: 0.9,
        side: THREE.DoubleSide,
        depthWrite: false
      });
      const ring = new THREE.Mesh(ringGeo, ringMat);
      ring.position.copy(p);
      // Orient the ring to face outward from the globe center.
      ring.lookAt(p.clone().multiplyScalar(2));
      this.globe.add(ring);

      this.beacons.push({ point: p, ring, dot, phase: Math.random() * Math.PI * 2 });
    }

    // 6. Data arcs — start with a couple in-flight.
    for (let i = 0; i < 3; i++) this.spawnArc(true);

    // 7. Orbiting satellites — small glowing spheres circling on tilted orbits.
    for (let i = 0; i < 3; i++) {
      const orbit = 1.42 + i * 0.11;
      const sat = new THREE.Mesh(
        new THREE.SphereGeometry(0.035, 16, 16),
        new THREE.MeshBasicMaterial({ color: i === 1 ? this.CYAN : this.WHITE })
      );

      // Trail ring showing the orbit path.
      const pathPoints: THREE.Vector3[] = [];
      const seg = 128;
      for (let s = 0; s <= seg; s++) {
        const a = (s / seg) * Math.PI * 2;
        pathPoints.push(new THREE.Vector3(Math.cos(a) * orbit, 0, Math.sin(a) * orbit));
      }
      const trail = new THREE.Line(
        new THREE.BufferGeometry().setFromPoints(pathPoints),
        new THREE.LineBasicMaterial({
          color: i === 1 ? this.CYAN : this.RED,
          transparent: true,
          opacity: 0.28
        })
      );
      const tilt = new THREE.Euler(
        (Math.random() - 0.5) * 1.4,
        (Math.random() - 0.5) * 1.4,
        (Math.random() - 0.5) * 0.6
      );
      trail.rotation.copy(tilt);

      this.outerGroup.add(trail);
      this.outerGroup.add(sat);

      this.satellites.push({
        mesh: sat,
        trail,
        orbit,
        tilt,
        speed: 0.35 + i * 0.14,
        offset: Math.random() * Math.PI * 2
      });
    }

    // 8. Outer counter-rotating HUD reticle (tick-marked ring far from the globe).
    const reticleGeo = new THREE.BufferGeometry();
    const rPts: number[] = [];
    const tickCount = 48;
    const rInner = 1.65;
    const rOuter = 1.72;
    for (let i = 0; i < tickCount; i++) {
      const a = (i / tickCount) * Math.PI * 2;
      const long = i % 4 === 0;
      const outer = long ? rOuter + 0.05 : rOuter;
      rPts.push(Math.cos(a) * rInner, Math.sin(a) * rInner, 0);
      rPts.push(Math.cos(a) * outer, Math.sin(a) * outer, 0);
    }
    reticleGeo.setAttribute('position', new THREE.Float32BufferAttribute(rPts, 3));
    this.reticle = new THREE.LineSegments(
      reticleGeo,
      new THREE.LineBasicMaterial({ color: this.RED, transparent: true, opacity: 0.65 })
    );
    this.reticle.rotation.x = Math.PI / 2;
    this.outerGroup.add(this.reticle);

    // Fixed axial tilt on the globe (looks alive from the start).
    this.globe.rotation.z = 0.36;

    this.scene.add(this.globe);
    this.scene.add(this.outerGroup);
    this.animate();
  }

  private sphericalTo(lat: number, lon: number, r: number): THREE.Vector3 {
    return new THREE.Vector3(
      r * Math.cos(lat) * Math.cos(lon),
      r * Math.sin(lat),
      r * Math.cos(lat) * Math.sin(lon)
    );
  }

  private spawnArc(instant = false): void {
    if (!this.beacons.length || !this.globe) return;
    const a = this.beacons[Math.floor(Math.random() * this.beacons.length)];
    let b = a;
    // Guarantee a different endpoint.
    while (b === a) b = this.beacons[Math.floor(Math.random() * this.beacons.length)];

    const mid = a.point.clone().add(b.point).multiplyScalar(0.5);
    // Push the midpoint outward for a nice cambered arc.
    const arcHeight = 0.4 + a.point.distanceTo(b.point) * 0.35;
    mid.normalize().multiplyScalar(1 + arcHeight);

    const curve = new THREE.QuadraticBezierCurve3(a.point.clone(), mid, b.point.clone());
    const geo = new THREE.BufferGeometry().setFromPoints(curve.getPoints(48));
    const mat = new THREE.LineBasicMaterial({
      color: Math.random() < 0.3 ? this.CYAN : this.WHITE,
      transparent: true,
      opacity: 0
    });
    const line = new THREE.Line(geo, mat);
    this.globe.add(line);

    // Traveling head — a small sphere that races along the curve.
    const head = new THREE.Mesh(
      new THREE.SphereGeometry(0.02, 10, 10),
      new THREE.MeshBasicMaterial({ color: mat.color })
    );
    this.globe.add(head);

    this.arcs.push({
      line,
      material: mat,
      head,
      curve,
      life: instant ? Math.random() * 0.6 : 0,
      ttl: 1.4 + Math.random() * 0.8
    });
  }

  private applyStatus(): void {
    if (!this.wireMaterial) return;
    switch (this.status) {
      case 'down':
        this.speed = 0.045;
        this.targetOpacity = 0.85;
        this.statusColor.copy(this.RED);
        break;
      case 'degraded':
        this.speed = 0.015;
        this.targetOpacity = 0.9;
        this.statusColor.lerpColors(this.RED, this.WHITE, 0.6);
        break;
      case 'up':
        this.targetOpacity = 0.55;
        this.statusColor.copy(this.RED);
        break;
      default:
        this.targetOpacity = 0.42;
        this.statusColor.copy(this.RED);
    }
  }

  private animate = (): void => {
    this.animFrameId = requestAnimationFrame(this.animate);
    const dt = Math.min(this.clock.getDelta(), 0.05);
    const t = performance.now() * 0.001;

    if (this.globe) this.globe.rotation.y += this.speed;
    this.speed += (this.baseSpeed - this.speed) * 0.05;

    if (this.wireMaterial) {
      this.wireMaterial.opacity += (this.targetOpacity - this.wireMaterial.opacity) * 0.08;
    }
    if (this.ring) this.ring.rotation.z += 0.003;
    if (this.reticle) this.reticle.rotation.z -= 0.004;

    // Voice UI: ease toward the current listening/voiceLevel inputs so state changes (wake
    // word heard, speech starts/stops) animate smoothly rather than snapping.
    this.smoothedListening += ((this.listening ? 1 : 0) - this.smoothedListening) * 0.08;
    this.smoothedVoiceLevel += (this.voiceLevel - this.smoothedVoiceLevel) * 0.15;

    if (this.wireMaterial) {
      // Blend FROM the resting status color every frame (not from whatever color is currently
      // on the material) so this always eases back to red/white as `smoothedListening` decays
      // to 0, instead of latching onto cyan permanently once a conversation starts.
      this.wireMaterial.color.lerpColors(this.statusColor, this.CYAN, this.smoothedListening * 0.5);
      if (this.smoothedListening > 0.01) {
        // Extra opacity proportional to actual voice level for a "brighter while talking" feel.
        const glowBoost = 0.25 + this.smoothedVoiceLevel * 0.4;
        this.wireMaterial.opacity = Math.min(1, this.wireMaterial.opacity + glowBoost * this.smoothedListening);
      }
    }

    // "Beat" — the whole globe scales in/out with actual detected speech volume. At zero
    // voice level (listening but silent) this settles back to 1 — glow only, no beat.
    if (this.globe) {
      const beat = 1 + this.smoothedListening * this.smoothedVoiceLevel * 0.09 * (1 + Math.sin(t * 9) * 0.15);
      this.globe.scale.setScalar(beat);
    }

    // Beacon shockwave rings — each pulses on its own phase.
    for (const b of this.beacons) {
      const p = (Math.sin(t * 1.6 + b.phase) + 1) / 2; // 0..1
      const scale = 0.5 + p * 2.4;
      b.ring.scale.setScalar(scale);
      (b.ring.material as THREE.MeshBasicMaterial).opacity = (1 - p) * 0.9;
      // Dot itself gently pulses too.
      const dotScale = 1 + Math.sin(t * 2.6 + b.phase) * 0.15;
      b.dot.scale.setScalar(dotScale);
    }

    // Data-arc animation — fade in, race a head along the curve, fade out.
    for (let i = this.arcs.length - 1; i >= 0; i--) {
      const a = this.arcs[i];
      a.life += dt;
      const u = Math.min(a.life / a.ttl, 1);
      // Sinusoidal opacity envelope.
      a.material.opacity = Math.sin(u * Math.PI) * 0.9;
      const pos = a.curve.getPoint(u);
      a.head.position.copy(pos);
      (a.head.material as THREE.MeshBasicMaterial).opacity = Math.sin(u * Math.PI);

      if (u >= 1) {
        this.globe!.remove(a.line);
        this.globe!.remove(a.head);
        a.line.geometry.dispose();
        a.material.dispose();
        a.head.geometry.dispose();
        (a.head.material as THREE.MeshBasicMaterial).dispose();
        this.arcs.splice(i, 1);
      }
    }
    // Keep ~4 arcs alive on average.
    if (this.arcs.length < 4 && Math.random() < 0.06) this.spawnArc();

    // Orbiting satellites.
    for (const s of this.satellites) {
      const angle = t * s.speed + s.offset;
      const local = new THREE.Vector3(Math.cos(angle) * s.orbit, 0, Math.sin(angle) * s.orbit);
      local.applyEuler(s.tilt);
      s.mesh.position.copy(local);
    }

    this.renderer!.render(this.scene!, this.camera!);
  };

  private resize(): void {
    if (!this.camera || !this.renderer) return;
    const parent = this.canvasRef.nativeElement.parentElement!;
    const w = parent.clientWidth || 320;
    const h = parent.clientHeight || 320;
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(w, h, false);
  }
}
