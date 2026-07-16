import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';

// Iron Man / JARVIS-style holographic HUD chrome — rotating tick-marked
// rings, a radar sweep, and a slow scanline. Pure SVG + CSS: no WebGL
// dependency, so it renders even where the Three.js starfield can't (no GPU
// passthrough in a VM/RDP session, WebGL disabled by policy, etc). This is
// the layer that guarantees "something is always visibly moving".
@Component({
  selector: 'app-hud-overlay',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './hud-overlay.component.html',
  styleUrl: './hud-overlay.component.css'
})
export class HudOverlayComponent {
  // Tick marks every 15° around the dial ring.
  readonly ticks = Array.from({ length: 24 }, (_, i) => i * 15);
  // Sparser tick marks for the smaller corner dial.
  readonly ticksSparse = Array.from({ length: 12 }, (_, i) => i * 30);
}
