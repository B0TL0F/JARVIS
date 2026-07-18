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

  // AI provider selector
  aiProvider = 'claude';
  savingProvider = false;
  providerSaved = false;

  // Claude AI
  claudeModel = 'claude-opus-4-8';
  claudeApiKey = '';
  claudeApiKeyConfigured = false;
  savingClaude = false;
  claudeSaved = false;

  // Gemini AI
  geminiModel = 'gemini-2.0-flash';
  geminiApiKey = '';
  geminiApiKeyConfigured = false;
  savingGemini = false;
  geminiSaved = false;

  // Groq AI
  groqModel = 'llama-3.3-70b-versatile';
  groqApiKey = '';
  groqApiKeyConfigured = false;
  savingGroq = false;
  groqSaved = false;

  // Alerts
  alertsEnabled = false;
  teamsWebhookUrl = '';
  teamsWebhookConfigured = false;
  smtpHost = '';
  smtpPort: number | null = 587;
  smtpUsername = '';
  smtpPassword = '';
  smtpPasswordConfigured = false;
  smtpFrom = '';
  smtpTo = '';
  savingAlerts = false;
  alertsSaved = false;

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

        this.aiProvider = s.aiProvider.provider;

        this.claudeModel = s.claude.model ?? 'claude-opus-4-8';
        this.claudeApiKeyConfigured = s.claude.apiKeyConfigured;

        this.geminiModel = s.gemini.model ?? 'gemini-2.0-flash';
        this.geminiApiKeyConfigured = s.gemini.apiKeyConfigured;

        this.groqModel = s.groq.model ?? 'llama-3.3-70b-versatile';
        this.groqApiKeyConfigured = s.groq.apiKeyConfigured;

        this.alertsEnabled = s.alerts.enabled;
        this.teamsWebhookConfigured = s.alerts.teamsWebhookConfigured;
        this.smtpHost = s.alerts.smtpHost ?? '';
        this.smtpPort = s.alerts.smtpPort ?? 587;
        this.smtpUsername = s.alerts.smtpUsername ?? '';
        this.smtpPasswordConfigured = s.alerts.smtpPasswordConfigured;
        this.smtpFrom = s.alerts.smtpFrom ?? '';
        this.smtpTo = s.alerts.smtpTo ?? '';

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

  saveProvider(): void {
    this.savingProvider = true;
    this.providerSaved = false;
    this.error = null;
    this.settings.updateAiProvider({ provider: this.aiProvider }).subscribe({
      next: () => {
        this.savingProvider = false;
        this.providerSaved = true;
        this.load();
      },
      error: () => { this.error = 'Failed to save AI provider.'; this.savingProvider = false; }
    });
  }

  saveGemini(): void {
    this.savingGemini = true;
    this.geminiSaved = false;
    this.error = null;
    this.settings.updateGemini({
      model: this.geminiModel || null,
      apiKey: this.geminiApiKey || null // blank keeps existing key
    }).subscribe({
      next: () => {
        this.savingGemini = false;
        this.geminiSaved = true;
        this.geminiApiKey = '';
        this.load();
      },
      error: () => { this.error = 'Failed to save Gemini AI settings.'; this.savingGemini = false; }
    });
  }

  saveGroq(): void {
    this.savingGroq = true;
    this.groqSaved = false;
    this.error = null;
    this.settings.updateGroq({
      model: this.groqModel || null,
      apiKey: this.groqApiKey || null // blank keeps existing key
    }).subscribe({
      next: () => {
        this.savingGroq = false;
        this.groqSaved = true;
        this.groqApiKey = '';
        this.load();
      },
      error: () => { this.error = 'Failed to save Groq AI settings.'; this.savingGroq = false; }
    });
  }

  saveClaude(): void {
    this.savingClaude = true;
    this.claudeSaved = false;
    this.error = null;
    this.settings.updateClaude({
      model: this.claudeModel || null,
      apiKey: this.claudeApiKey || null // blank keeps existing key
    }).subscribe({
      next: () => {
        this.savingClaude = false;
        this.claudeSaved = true;
        this.claudeApiKey = '';
        this.load();
      },
      error: () => { this.error = 'Failed to save Claude AI settings.'; this.savingClaude = false; }
    });
  }

  saveAlerts(): void {
    this.savingAlerts = true;
    this.alertsSaved = false;
    this.error = null;
    this.settings.updateAlerts({
      enabled: this.alertsEnabled,
      teamsWebhookUrl: this.teamsWebhookUrl || null, // blank keeps existing webhook
      smtpHost: this.smtpHost || null,
      smtpPort: this.smtpPort,
      smtpUsername: this.smtpUsername || null,
      smtpPassword: this.smtpPassword || null, // blank keeps existing password
      smtpFrom: this.smtpFrom || null,
      smtpTo: this.smtpTo || null
    }).subscribe({
      next: () => {
        this.savingAlerts = false;
        this.alertsSaved = true;
        this.teamsWebhookUrl = '';
        this.smtpPassword = '';
        this.load();
      },
      error: () => { this.error = 'Failed to save alert settings.'; this.savingAlerts = false; }
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
