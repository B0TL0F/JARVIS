import { AfterViewInit, Component, ElementRef, Input, OnChanges, OnDestroy, SimpleChanges, ViewChild } from '@angular/core';
import * as THREE from 'three';

// The HUD-mode globe centerpiece — ported 1:1 from the reference design's
// hud/globe.js (five-layer sphere: solid core, wireframe shell, points
// layer, equator ring, plus a fixed axial tilt) instead of the ambient
// ThreeBackgroundComponent's starfield. Container-sized via ResizeObserver,
// not the window, since it lives inside a HUD grid tile.
@Component({
  selector: 'app-hud-globe',
  standalone: true,
  template: `<canvas #canvas class="globe-canvas" aria-hidden="true"></canvas>`,
  styleUrl: './hud-globe.component.css'
})
export class HudGlobeComponent implements AfterViewInit, OnChanges, OnDestroy {
  @ViewChild('canvas', { static: true }) canvasRef!: ElementRef<HTMLCanvasElement>;

  // Reused as the reference's assistant state (idle/listening/thinking) —
  // our fleet status drives the same speed/opacity/color reactions.
  @Input() status: 'up' | 'degraded' | 'down' | 'unknown' = 'unknown';

  private renderer?: THREE.WebGLRenderer;
  private scene?: THREE.Scene;
  private camera?: THREE.PerspectiveCamera;
  private globe?: THREE.Group;
  private wire?: THREE.LineSegments;
  private wireMaterial?: THREE.LineBasicMaterial;
  private points?: THREE.Points;
  private ring?: THREE.Mesh;
  private resizeObserver?: ResizeObserver;
  private animFrameId?: number;

  private speed = 0.0016;
  private readonly baseSpeed = 0.0016;
  private targetOpacity = 0.42;

  private readonly RED = new THREE.Color(0xff2b2b);
  private readonly WHITE = new THREE.Color(0xffffff);

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
    this.camera.position.z = 3.0;

    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(w, h, false);

    this.globe = new THREE.Group();

    // 1. Solid core — occludes the back half of the wireframe shell.
    const core = new THREE.Mesh(
      new THREE.SphereGeometry(0.98, 48, 48),
      new THREE.MeshBasicMaterial({ color: 0x0a0608 })
    );
    this.globe.add(core);

    // 2. Wireframe shell.
    this.wireMaterial = new THREE.LineBasicMaterial({ color: this.RED, transparent: true, opacity: 0.42 });
    this.wire = new THREE.LineSegments(new THREE.WireframeGeometry(new THREE.SphereGeometry(1, 28, 20)), this.wireMaterial);
    this.globe.add(this.wire);

    // 3. Points layer.
    this.points = new THREE.Points(
      new THREE.SphereGeometry(1.01, 40, 30),
      new THREE.PointsMaterial({ color: this.WHITE, size: 0.012, transparent: true, opacity: 0.8 })
    );
    this.globe.add(this.points);

    // 4. Equator ring.
    this.ring = new THREE.Mesh(
      new THREE.RingGeometry(1.18, 1.2, 96),
      new THREE.MeshBasicMaterial({ color: this.RED, transparent: true, opacity: 0.5, side: THREE.DoubleSide })
    );
    this.ring.rotation.x = Math.PI / 2.2;
    this.globe.add(this.ring);

    // Fixed axial tilt.
    this.globe.rotation.z = 0.36;

    this.scene.add(this.globe);
    this.animate();
  }

  private applyStatus(): void {
    if (!this.wireMaterial) return;
    switch (this.status) {
      case 'down':
        this.speed = 0.03;
        this.targetOpacity = 0.7;
        this.wireMaterial.color.copy(this.RED);
        break;
      case 'degraded':
        this.speed = 0.012;
        this.targetOpacity = 0.85;
        this.wireMaterial.color.copy(this.WHITE);
        break;
      default:
        this.targetOpacity = 0.42;
        this.wireMaterial.color.copy(this.RED);
        break;
    }
  }

  private animate = (): void => {
    this.animFrameId = requestAnimationFrame(this.animate);

    if (this.globe) {
      this.globe.rotation.y += this.speed;
    }
    this.speed += (this.baseSpeed - this.speed) * 0.05;

    if (this.wireMaterial) {
      this.wireMaterial.opacity += (this.targetOpacity - this.wireMaterial.opacity) * 0.08;
    }
    if (this.ring) {
      this.ring.rotation.z += 0.002;
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
