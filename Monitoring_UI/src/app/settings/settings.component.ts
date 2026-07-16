import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ReimportResult, SettingsService } from '../services/settings.service';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.css'
})
export class SettingsComponent implements OnInit {
  loading = true;
  error: string | null = null;

  // Azure DevOps form
  org = '';
  project = '';
  apiVersion = '';
  pat = '';
  patConfigured = false;
  savingAzure = false;
  azureSaved = false;

  // Ocelot
  ocelotDir = '';
  reimporting = false;
  reimportResult: ReimportResult | null = null;

  constructor(private settings: SettingsService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.settings.get().subscribe({
      next: (s) => {
        this.org = s.azureDevOps.org ?? '';
        this.project = s.azureDevOps.project ?? '';
        this.apiVersion = s.azureDevOps.apiVersion ?? '';
        this.patConfigured = s.azureDevOps.patConfigured;
        this.ocelotDir = s.ocelot.directory;
        this.loading = false;
      },
      error: () => { this.error = 'Failed to load settings.'; this.loading = false; }
    });
  }

  saveAzure(): void {
    this.savingAzure = true;
    this.azureSaved = false;
    this.error = null;
    this.settings.updateAzure({
      org: this.org || null,
      project: this.project || null,
      apiVersion: this.apiVersion || null,
      pat: this.pat || null   // blank keeps existing PAT
    }).subscribe({
      next: () => {
        this.savingAzure = false;
        this.azureSaved = true;
        this.pat = '';
        this.load();
      },
      error: () => { this.error = 'Failed to save Azure DevOps settings.'; this.savingAzure = false; }
    });
  }

  reimport(force: boolean): void {
    if (force && !confirm('Force re-import clears all previously imported targets and rebuilds from the current files. Continue?')) {
      return;
    }
    this.reimporting = true;
    this.reimportResult = null;
    this.error = null;
    this.settings.reimportOcelot(force).subscribe({
      next: (r) => { this.reimportResult = r; this.reimporting = false; },
      error: () => { this.error = 'Re-import failed.'; this.reimporting = false; }
    });
  }
}
