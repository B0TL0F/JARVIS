import {
  AfterViewInit,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  ViewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Subscription } from 'rxjs';
import { StatusService } from '../services/status.service';
import { AuthService } from '../services/auth.service';
import { AnimationService } from '../services/animation.service';
import { HistoryService } from '../services/history.service';
import { ServiceStatus, StatusResponse } from '../models/status.model';
import { ModuleInsight } from '../models/history.model';
import { MagneticDirective } from '../shared/magnetic.directive';
import { HudGlobeComponent } from '../shared/hud-globe/hud-globe.component';

type Severity = 'down' | 'degraded' | 'up' | 'unknown';
type FleetStatus = 'up' | 'degraded' | 'down' | 'unknown';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, MagneticDirective, HudGlobeComponent],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.css'
})
export class DashboardComponent implements OnInit, AfterViewInit, OnDestroy {
  status: StatusResponse | null = null;
  sortedModules: ServiceStatus[] = [];
  loading = true;
  lastError = false;

  environments: string[] = [];
  selectedEnvironment: string | null = null;

  counts = { up: 0, degraded: 0, down: 0 };
  fleetStatus: FleetStatus = 'unknown';
  statusHeadline = 'READING TELEMETRY…';

  // Heuristic insight flags (flaky / anomalous response time / trending),
  // keyed by module — see HistoryComponent for the full picture and why.
  insightsByModule: Record<string, ModuleInsight> = {};

  @ViewChild('headline') headlineRef?: ElementRef<HTMLElement>;

  private sub?: Subscription;
  private headlineRevealed = false;
  private readonly severityRank: Record<Severity, number> = { down: 0, degraded: 1, unknown: 2, up: 3 };

  constructor(
    private statusService: StatusService,
    public auth: AuthService,
    private anim: AnimationService,
    private history: HistoryService,
    private hostRef: ElementRef<HTMLElement>
  ) {}

  async ngOnInit(): Promise<void> {
    this.environments = await this.statusService.loadEnvironments();

    this.sub = this.statusService.environment$.subscribe((env) => {
      this.selectedEnvironment = env;
      this.loadInsights(env);
    });

    this.sub.add(
      this.statusService.watchStatus().subscribe((result) => {
        this.loading = false;
        this.lastError = result === null;
        if (result !== null) {
          this.status = result;
          this.recompute(result);
        }
      })
    );
  }

  ngAfterViewInit(): void {
    this.anim.revealIn(this.hostRef.nativeElement);
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  onEnvironmentChange(name: string): void {
    this.loading = true;
    this.status = null;
    this.statusService.selectEnvironment(name);
  }

  severity(module: ServiceStatus): Severity {
    const checks = [module.api, module.db].filter((c) => c !== null) as { isUp: boolean }[];
    if (checks.length === 0) return 'unknown';
    if (checks.every((c) => c.isUp)) return 'up';
    if (checks.every((c) => !c.isUp)) return 'down';
    return 'degraded';
  }

  checkClass(check: { isUp: boolean } | null): string {
    if (!check) return 'na';
    return check.isUp ? 'up' : 'down';
  }

  insightFor(module: string): ModuleInsight | null {
    return this.insightsByModule[module] ?? null;
  }

  // HUD "Attention" tile — modules that aren't fully up, worst first.
  get attentionModules(): ServiceStatus[] {
    return this.sortedModules.filter((m) => this.severity(m) !== 'up');
  }

  private loadInsights(env: string | null): void {
    if (!env) return;
    this.history.getInsights(env).subscribe({
      next: (insights) => {
        this.insightsByModule = Object.fromEntries(insights.map((i) => [i.module, i]));
      },
      error: () => {
        // Insight badges are a nice-to-have — a failure here shouldn't block the dashboard.
      }
    });
  }

  trackByModule(_index: number, item: ServiceStatus): string {
    return item.module;
  }

  private recompute(result: StatusResponse): void {
    this.counts = { up: 0, degraded: 0, down: 0 };
    for (const m of result.modules) {
      const s = this.severity(m);
      if (s === 'up') this.counts.up++;
      else if (s === 'degraded') this.counts.degraded++;
      else if (s === 'down') this.counts.down++;
    }

    this.sortedModules = [...result.modules].sort(
      (a, b) => this.severityRank[this.severity(a)] - this.severityRank[this.severity(b)]
    );

    if (this.counts.down > 0) {
      this.fleetStatus = 'down';
      this.statusHeadline = `${this.counts.down} SERVICE${this.counts.down === 1 ? '' : 'S'} DOWN`;
    } else if (this.counts.degraded > 0) {
      this.fleetStatus = 'degraded';
      this.statusHeadline = `${this.counts.degraded} SERVICE${this.counts.degraded === 1 ? '' : 'S'} DEGRADED`;
    } else {
      this.fleetStatus = 'up';
      this.statusHeadline = 'ALL SYSTEMS NOMINAL';
    }

    // Defer to the next tick so *ngIf/interpolation have rendered the new DOM
    // before we read/animate it.
    setTimeout(() => {
      if (!this.headlineRevealed) {
        this.headlineRevealed = true;
        this.anim.revealHeadline(this.headlineRef?.nativeElement);
      }
      if (this.status) {
        // Every row must be visible without scrolling — this is monitoring
        // data, not a portfolio reveal — so stagger everything in at once.
        this.anim.staggerIn(this.hostRef.nativeElement, '.hud-module-row');
      }
    });
  }
}
