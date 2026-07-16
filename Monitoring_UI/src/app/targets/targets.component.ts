import { Component, ElementRef, HostListener, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ManagedTarget, TargetUpsert, TargetsService } from '../services/targets.service';
import { AnimationService } from '../services/animation.service';
import { MagneticDirective } from '../shared/magnetic.directive';

interface EnvGroup {
  environment: string;
  targets: ManagedTarget[];
  expanded: boolean;
}

@Component({
  selector: 'app-targets',
  standalone: true,
  imports: [CommonModule, FormsModule, MagneticDirective],
  templateUrl: './targets.component.html',
  styleUrl: './targets.component.css'
})
export class TargetsComponent implements OnInit {
  targets: ManagedTarget[] = [];
  groups: EnvGroup[] = [];
  loading = true;
  error: string | null = null;
  saving = false;

  // Filters by environment name or module name (case-insensitive substring).
  search = '';

  // Editor form state. editingId = null && !editingIsOverride → creating a
  // brand new target. editingId = null && editingIsOverride → editing a
  // config-only target for the first time (save creates an override row).
  editingId: number | null = null;
  editingIsOverride = false;
  showForm = false;
  form: TargetUpsert = this.blankForm();

  constructor(
    private svc: TargetsService,
    private anim: AnimationService,
    private hostRef: ElementRef<HTMLElement>
  ) {}

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.showForm) this.cancel();
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.svc.list().subscribe({
      next: (t) => {
        this.targets = t;
        this.rebuildGroups();
        this.loading = false;
        setTimeout(() => this.anim.staggerIn(this.hostRef.nativeElement, '.group'));
      },
      error: () => { this.error = 'Failed to load targets.'; this.loading = false; }
    });
  }

  private rebuildGroups(): void {
    // Preserve expand/collapse state across reloads by environment name.
    const wasExpanded = new Map(this.groups.map(g => [g.environment, g.expanded]));
    const byEnv = new Map<string, ManagedTarget[]>();
    for (const t of this.targets) {
      const list = byEnv.get(t.environment) ?? [];
      list.push(t);
      byEnv.set(t.environment, list);
    }
    this.groups = Array.from(byEnv.entries())
      .sort((a, b) => a[0].localeCompare(b[0]))
      .map(([environment, targets]) => ({
        environment,
        targets: targets.sort((a, b) => a.module.localeCompare(b.module)),
        expanded: wasExpanded.get(environment) ?? true
      }));
  }

  get filteredGroups(): EnvGroup[] {
    const q = this.search.trim().toLowerCase();
    if (!q) return this.groups;
    return this.groups
      .map(g => ({
        ...g,
        targets: g.environment.toLowerCase().includes(q)
          ? g.targets
          : g.targets.filter(t => t.module.toLowerCase().includes(q))
      }))
      .filter(g => g.targets.length > 0);
  }

  toggle(group: EnvGroup): void {
    group.expanded = !group.expanded;
  }

  expandAll(expand: boolean): void {
    this.groups.forEach(g => g.expanded = expand);
  }

  blankForm(): TargetUpsert {
    return { environment: '', module: '', apiHost: null, routePrefix: null };
  }

  startCreate(envHint?: string): void {
    this.editingId = null;
    this.editingIsOverride = false;
    this.form = this.blankForm();
    if (envHint) this.form.environment = envHint;
    this.error = null;
    this.showForm = true;
  }

  startEdit(t: ManagedTarget): void {
    this.editingId = t.id;
    this.editingIsOverride = t.id === null; // config-only target, never edited before
    this.form = {
      environment: t.environment,
      module: t.module,
      apiHost: t.apiHost || null,
      routePrefix: t.routePrefix || null
    };
    this.error = null;
    this.showForm = true;
  }

  cancel(): void {
    this.showForm = false;
    this.editingId = null;
    this.editingIsOverride = false;
  }

  save(): void {
    this.saving = true;
    this.error = null;
    const done = {
      next: () => { this.saving = false; this.showForm = false; this.editingId = null; this.editingIsOverride = false; this.load(); },
      error: (err: any) => { this.error = err?.error?.error ?? 'Save failed.'; this.saving = false; }
    };
    if (this.editingIsOverride) {
      this.svc.override(this.form).subscribe(done);
    } else if (this.editingId === null) {
      this.svc.create(this.form).subscribe(done);
    } else {
      this.svc.update(this.editingId, this.form).subscribe(done);
    }
  }

  remove(t: ManagedTarget): void {
    if (t.id === null) return; // config-only target — nothing to delete, edit it instead
    if (!confirm(`Delete target "${t.module}" in "${t.environment}"?`)) return;
    this.error = null;
    this.svc.remove(t.id).subscribe({
      next: () => this.load(),
      error: (err) => this.error = err?.error?.error ?? 'Delete failed.'
    });
  }

  // Removes the environment and every target/module inside it — the whole
  // group disappears from here and from the dashboard's environment tabs.
  removeEnvironment(g: EnvGroup): void {
    const count = g.targets.length;
    if (!confirm(`Delete environment "${g.environment}" and all ${count} target${count === 1 ? '' : 's'} in it? This cannot be undone.`)) return;
    this.error = null;
    this.svc.removeEnvironment(g.environment).subscribe({
      next: () => this.load(),
      error: (err) => this.error = err?.error?.error ?? 'Failed to delete environment.'
    });
  }

  trackByEnv = (_i: number, g: EnvGroup) => g.environment;
  trackById = (_i: number, t: ManagedTarget) => t.id ?? `${t.environment}:${t.module}`;
}
