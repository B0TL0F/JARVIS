import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AdminService } from '../services/admin.service';
import { ActivityLog } from '../models/admin.model';

@Component({
  selector: 'app-activity',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './activity.component.html',
  styleUrl: './admin.css'
})
export class ActivityComponent implements OnInit {
  items: ActivityLog[] = [];
  page = 1;
  pageSize = 50;
  total = 0;
  loading = true;
  error: string | null = null;

  constructor(private admin: AdminService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.admin.getActivity(this.page, this.pageSize).subscribe({
      next: (r) => {
        this.items = r.items;
        this.total = r.total;
        this.loading = false;
      },
      error: () => { this.error = 'Failed to load activity log.'; this.loading = false; }
    });
  }

  get totalPages(): number {
    return Math.max(1, Math.ceil(this.total / this.pageSize));
  }

  prev(): void { if (this.page > 1) { this.page--; this.load(); } }
  next(): void { if (this.page < this.totalPages) { this.page++; this.load(); } }

  trackById = (_i: number, a: ActivityLog) => a.id;
}
