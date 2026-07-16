import { AfterViewInit, Component, ElementRef, OnDestroy, ViewChild } from '@angular/core';
import * as THREE from 'three';

// Ambient HUD backdrop — a slow star-field drift, three barely-visible
// orbital rings, and a breathing wireframe ground plane. Ported from
// Sentinel's JarvisThreeBackground.vue. Purely decorative (aria-hidden),
// transparent, and deliberately subtle so it never competes with real data.
@Component({
  selector: 'app-three-background',
  standalone: true,
  template: `<canvas #canvas class="three-canvas" aria-hidden="true"></canvas>`,
  styleUrl: './three-background.component.css'
})
export class ThreeBackgroundComponent implements AfterViewInit, OnDestroy {
  @ViewChild('canvas', { static: true }) canvasRef!: ElementRef<HTMLCanvasElement>;

  private renderer?: THREE.WebGLRenderer;
  private scene?: THREE.Scene;
  private camera?: THREE.PerspectiveCamera;
  private clock?: THREE.Clock;
  private starField?: THREE.Points;
  private rings: { mesh: THREE.Mesh; speed: number }[] = [];
  private grid?: THREE.Mesh;
  private animFrameId?: number;
  private readonly onResize = () => this.resize();

  ngAfterViewInit(): void {
    // Gentle drift, not scroll/parallax motion — stays on for everyone,
    // including reduced-motion users (rotation speeds are already ~0.005
    // rad/frame, about as subtle as ambience gets).
    try {
      this.init();
      window.addEventListener('resize', this.onResize);
    } catch (err) {
      // WebGL unavailable (e.g. no GPU passthrough in a VM/RDP session) —
      // fail silently; the CSS/SVG HUD overlay still provides motion.
      console.warn('3D background unavailable, falling back to HUD overlay only.', err);
    }
  }

  ngOnDestroy(): void {
    if (this.animFrameId) cancelAnimationFrame(this.animFrameId);
    window.removeEventListener('resize', this.onResize);
    this.renderer?.dispose();
  }

  private init(): void {
    const canvas = this.canvasRef.nativeElement;
    const w = window.innerWidth;
    const h = window.innerHeight;

    this.scene = new THREE.Scene();

    this.camera = new THREE.PerspectiveCamera(55, w / h, 0.1, 1000);
    this.camera.position.set(0, 3, 12);
    this.camera.lookAt(0, 0, 0);

    this.renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: false });
    this.renderer.setSize(w, h);
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 1.2));
    this.renderer.setClearColor(0x000000, 0);

    this.clock = new THREE.Clock();

    this.buildStars();
    this.buildRings();
    this.buildGrid();
    this.animate();
  }

  private buildStars(): void {
    const count = 350;
    const positions = new Float32Array(count * 3);
    for (let i = 0; i < count; i++) {
      positions[i * 3] = (Math.random() - 0.5) * 50;
      positions[i * 3 + 1] = (Math.random() - 0.5) * 30;
      positions[i * 3 + 2] = (Math.random() - 0.5) * 40;
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    const mat = new THREE.PointsMaterial({
      color: 0xff6a6a,
      size: 0.06,
      transparent: true,
      opacity: 0.45,
      sizeAttenuation: true,
      depthWrite: false
    });
    this.starField = new THREE.Points(geo, mat);
    this.scene!.add(this.starField);
  }

  private buildRings(): void {
    const configs = [
      { radius: 5.5, tilt: 0.35, opacity: 0.07, color: 0xff2b2b, speed: 0.06 },
      { radius: 8.5, tilt: -0.25, opacity: 0.05, color: 0xcc0000, speed: 0.04 },
      { radius: 11.5, tilt: 0.1, opacity: 0.03, color: 0x7a0000, speed: 0.025 }
    ];
    for (const cfg of configs) {
      const geo = new THREE.TorusGeometry(cfg.radius, 0.012, 6, 180);
      const mat = new THREE.MeshBasicMaterial({ color: cfg.color, transparent: true, opacity: cfg.opacity });
      const mesh = new THREE.Mesh(geo, mat);
      mesh.rotation.x = Math.PI / 2 + cfg.tilt;
      mesh.position.y = -1.5;
      this.rings.push({ mesh, speed: cfg.speed });
      this.scene!.add(mesh);
    }
  }

  private buildGrid(): void {
    const geo = new THREE.PlaneGeometry(28, 18, 20, 14);
    const mat = new THREE.MeshBasicMaterial({ color: 0xff2b2b, wireframe: true, transparent: true, opacity: 0.03 });
    this.grid = new THREE.Mesh(geo, mat);
    this.grid.rotation.x = -Math.PI / 2.3;
    this.grid.position.y = -5;
    this.grid.position.z = -1;
    this.scene!.add(this.grid);
  }

  private animate = (): void => {
    this.animFrameId = requestAnimationFrame(this.animate);
    const t = this.clock!.getElapsedTime();

    if (this.starField) {
      this.starField.rotation.y = t * 0.008;
      this.starField.rotation.x = Math.sin(t * 0.004) * 0.02;
    }
    for (const { mesh, speed } of this.rings) {
      mesh.rotation.z += speed * 0.005;
    }
    if (this.grid) {
      (this.grid.material as THREE.MeshBasicMaterial).opacity = 0.025 + Math.sin(t * 0.5) * 0.008;
    }
    if (this.camera) {
      this.camera.position.x = Math.sin(t * 0.07) * 0.4;
      this.camera.lookAt(0, 0, 0);
    }
    this.renderer!.render(this.scene!, this.camera!);
  };

  private resize(): void {
    if (!this.camera || !this.renderer) return;
    const w = window.innerWidth;
    const h = window.innerHeight;
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(w, h);
  }
}
