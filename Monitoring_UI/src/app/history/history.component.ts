import { Component, ElementRef, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription, forkJoin } from 'rxjs';
import { StatusService } from '../services/status.service';
import { HistoryService } from '../services/history.service';
import { AnimationService } from '../services/animation.service';
import { CheckBucket, Incident, ModuleInsight } from '../models/history.model';
import { MagneticDirective } from '../shared/magnetic.directive';

interface ModuleHistory {
  module: string;
  insight: ModuleInsight | null;
  incidents: Incident[];
  buckets: CheckBucket[];
}

@Component({
  selector: 'app-history',
  standalone: true,
  imports: [CommonModule, FormsModule, MagneticDirective],
  templateUrl: './history.component.html',
  styleUrl: './history.component.css'
})
export class HistoryComponent implements OnInit, OnDestroy {
  environments: string[] = [];
  selectedEnvironment: string | null = null;
  loading = true;
  error: string | null = null;
  search = '';

  modules: ModuleHistory[] = [];
  flaggedInsights: ModuleInsight[] = [];

  private sub?: Subscription;

  constructor(
    private statusService: StatusService,
    private historyService: HistoryService,
    private anim: AnimationService,
    private hostRef: ElementRef<HTMLElement>
  ) {}

  async ngOnInit(): Promise<void> {
    this.environments = await this.statusService.loadEnvironments();
    this.sub = this.statusService.environment$.subscribe((env) => {
      this.selectedEnvironment = env;
      if (env) this.load(env);
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  onEnvironmentChange(name: string): void {
    this.statusService.selectEnvironment(name);
  }

  get filteredModules(): ModuleHistory[] {
    const q = this.search.trim().toLowerCase();
    if (!q) return this.modules;
    return this.modules.filter((m) => m.module.toLowerCase().includes(q));
  }

  trackByModule(_i: number, m: ModuleHistory): string {
    return m.module;
  }

  trackByIncident(_i: number, inc: Incident): string {
    return `${inc.module}-${inc.checkType}-${inc.startedAtUtc}`;
  }

  private load(environment: string): void {
    this.loading = true;
    this.error = null;

    forkJoin({
      insights: this.historyService.getInsights(environment),
      incidents: this.historyService.getIncidents(environment)
    }).subscribe({
      next: ({ insights, incidents }) => {
        this.flaggedInsights = insights.filter((i) => i.isFlaky || i.isResponseTimeAnomalous || i.trend !== 'stable');

        const incidentsByModule = new Map<string, Incident[]>();
        for (const inc of incidents) {
          const list = incidentsByModule.get(inc.module) ?? [];
          list.push(inc);
          incidentsByModule.set(inc.module, list);
        }

        this.modules = insights.map((insight) => ({
          module: insight.module,
          insight,
          incidents: incidentsByModule.get(insight.module) ?? [],
          buckets: []
        }));

        this.loading = false;
        setTimeout(() => this.anim.staggerIn(this.hostRef.nativeElement, '.module-card'));

        // Timeline bars are a nice-to-have visual, loaded after the main
        // content so a slow/empty environment never blocks the page.
        this.loadTimelines(environment);
      },
      error: () => {
        this.error = 'Failed to load history.';
        this.loading = false;
      }
    });
  }

  private loadTimelines(environment: string): void {
    const calls = this.modules.map((m) =>
      this.historyService.getChecks(environment, m.module, 'Api', 24)
    );
    if (!calls.length) return;
    forkJoin(calls).subscribe({
      next: (results) => {
        results.forEach((buckets, i) => (this.modules[i].buckets = buckets));
      },
      error: () => {
        // Timeline bars are decorative — a failure here shouldn't surface an error banner.
      }
    });
  }
}
