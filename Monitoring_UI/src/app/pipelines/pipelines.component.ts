import { Component, ElementRef, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { PipelineService } from '../services/pipeline.service';
import { AnimationService } from '../services/animation.service';
import { AuthService } from '../services/auth.service';
import { BuildErrorRecord, BuildPipeline, PipelineDashboard, ReleasePipeline, TriggeredBuild } from '../models/pipeline.model';
import { MagneticDirective } from '../shared/magnetic.directive';

@Component({
  selector: 'app-pipelines',
  standalone: true,
  imports: [CommonModule, FormsModule, MagneticDirective],
  templateUrl: './pipelines.component.html',
  styleUrl: './pipelines.component.css'
})
export class PipelinesComponent implements OnInit {
  data: PipelineDashboard | null = null;
  loading = true;
  fetchError = false;

  // buildId -> expanded error records (lazy-loaded on click)
  expandedErrors: Record<number, BuildErrorRecord[]> = {};
  loadingErrors: Record<number, boolean> = {};

  // Admin-only bulk build-trigger — the one write operation this app performs
  // against real Azure DevOps infrastructure. Selection is always manual,
  // never pre-checked/select-all, and always confirmed before executing.
  selectedDefinitionIds = new Set<number>();
  triggerBranch = '';
  triggering = false;
  triggerResults: TriggeredBuild[] = [];

  constructor(
    private pipelines: PipelineService,
    private anim: AnimationService,
    public auth: AuthService,
    private hostRef: ElementRef<HTMLElement>
  ) {}

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.loading = true;
    this.fetchError = false;
    this.pipelines.getDashboard().subscribe({
      next: (d) => {
        this.data = d;
        this.loading = false;
        setTimeout(() => this.anim.staggerIn(this.hostRef.nativeElement, '.card'));
      },
      error: () => {
        this.fetchError = true;
        this.loading = false;
      }
    });
  }

  toggleErrors(buildId: number | null): void {
    if (buildId === null) return;
    if (this.expandedErrors[buildId]) {
      delete this.expandedErrors[buildId];
      return;
    }
    this.loadingErrors[buildId] = true;
    this.pipelines.getBuildErrors(buildId).subscribe({
      next: (records) => {
        this.expandedErrors[buildId] = records;
        this.loadingErrors[buildId] = false;
      },
      error: () => {
        this.expandedErrors[buildId] = [];
        this.loadingErrors[buildId] = false;
      }
    });
  }

  statusClass(status: string | null): string {
    switch ((status ?? '').toLowerCase()) {
      case 'succeeded': return 'ok';
      case 'partiallysucceeded': return 'warn';
      case 'inprogress': return 'progress';
      case 'failed':
      case 'rejected': return 'fail';
      case 'canceled':
      case 'cancelled': return 'muted';
      default: return 'muted';
    }
  }

  trackBuild = (_i: number, p: BuildPipeline) => p.id;
  trackRelease = (_i: number, p: ReleasePipeline) => p.id;

  isSelected(id: number): boolean {
    return this.selectedDefinitionIds.has(id);
  }

  toggleSelected(id: number, checked: boolean): void {
    if (checked) this.selectedDefinitionIds.add(id);
    else this.selectedDefinitionIds.delete(id);
  }

  clearSelection(): void {
    this.selectedDefinitionIds.clear();
    this.triggerResults = [];
  }

  triggerSelected(): void {
    if (!this.data || this.selectedDefinitionIds.size === 0) return;

    const selected = this.data.buildPipelines.filter((p) => this.selectedDefinitionIds.has(p.id));
    const names = selected.map((p) => `  • ${p.name}`).join('\n');
    const branchNote = this.triggerBranch.trim() ? `\n\nBranch: ${this.triggerBranch.trim()}` : '\n\n(each pipeline\'s default branch)';
    const confirmed = confirm(
      `Trigger ${selected.length} build${selected.length === 1 ? '' : 's'} in Azure DevOps?\n\n${names}${branchNote}\n\nThis queues real builds and cannot be undone.`
    );
    if (!confirmed) return;

    this.triggering = true;
    this.triggerResults = [];
    this.pipelines.triggerBuilds(Array.from(this.selectedDefinitionIds), this.triggerBranch.trim()).subscribe({
      next: (results) => {
        this.triggerResults = results;
        this.triggering = false;
        this.selectedDefinitionIds.clear();
        // Give Azure a moment to register the new builds, then refresh so
        // they show up as "in progress" on the dashboard.
        setTimeout(() => this.refresh(), 1500);
      },
      error: () => {
        this.triggering = false;
      }
    });
  }
}
