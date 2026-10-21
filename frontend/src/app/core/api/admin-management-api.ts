import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-config';
import { queryParams } from './http-params';
import {
  AdminOrganization,
  AdminOrganizationDetail,
  AdminOrganizationQuery,
  AdminUser,
  AdminUserAction,
  AdminUserDetail,
  AdminUserQuery,
  MemberRole,
  OrganizationProfileInput,
  Paged,
} from './models';

/**
 * Platform administration of companies (Unternehmen) and user accounts (Benutzer): `/api/admin/organizations` and
 * `/api/admin/users`. Called without X-Organization-Id (see the auth interceptor). Writes are audited server-side with the
 * reason; opening a detail view is access-logged.
 */
@Injectable({ providedIn: 'root' })
export class AdminManagementApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly orgs = `${this.base}/api/admin/organizations`;
  private readonly users = `${this.base}/api/admin/users`;

  // ------------------------------------------------------------------ companies

  companies(q: AdminOrganizationQuery): Observable<Paged<AdminOrganization>> {
    return this.http.get<Paged<AdminOrganization>>(this.orgs, { params: queryParams(q) });
  }

  company(id: string): Observable<AdminOrganizationDetail> {
    return this.http.get<AdminOrganizationDetail>(`${this.orgs}/${id}`);
  }

  updateCompany(
    id: string,
    organization: OrganizationProfileInput,
    version: number,
    reason: string | null,
  ): Observable<AdminOrganizationDetail> {
    return this.http.put<AdminOrganizationDetail>(`${this.orgs}/${id}`, {
      organization,
      version,
      reason,
    });
  }

  setStatus(
    id: string,
    action: 'verify' | 'reject' | 'suspend' | 'reactivate',
    reason: string | null,
  ): Observable<AdminOrganization> {
    return this.http.post<AdminOrganization>(
      `${this.orgs}/${id}/${action}`,
      action === 'verify' ? {} : { reason },
    );
  }

  setModules(id: string, modules: string[]): Observable<AdminOrganization> {
    return this.http.put<AdminOrganization>(`${this.orgs}/${id}/modules`, { modules });
  }

  changeMemberRole(
    id: string,
    userId: string,
    role: MemberRole,
    reason: string | null,
  ): Observable<AdminOrganizationDetail> {
    return this.http.put<AdminOrganizationDetail>(`${this.orgs}/${id}/members/${userId}/role`, {
      role,
      reason,
    });
  }

  removeMember(id: string, userId: string, reason: string): Observable<AdminOrganizationDetail> {
    return this.http.post<AdminOrganizationDetail>(`${this.orgs}/${id}/members/${userId}/remove`, {
      reason,
    });
  }

  transferOwnership(
    id: string,
    userId: string,
    reason: string | null,
  ): Observable<AdminOrganizationDetail> {
    return this.http.post<AdminOrganizationDetail>(`${this.orgs}/${id}/transfer-ownership`, {
      userId,
      reason,
    });
  }

  // ------------------------------------------------------------------ users

  userList(q: AdminUserQuery): Observable<Paged<AdminUser>> {
    return this.http.get<Paged<AdminUser>>(this.users, { params: queryParams(q) });
  }

  user(id: string): Observable<AdminUserDetail> {
    return this.http.get<AdminUserDetail>(`${this.users}/${id}`);
  }

  userAction(
    id: string,
    action: AdminUserAction,
    reason: string | null,
    language?: string,
  ): Observable<AdminUserDetail> {
    const body =
      action === 'password-reset' || action === 'resend-verification'
        ? { reason, language }
        : { reason };
    return this.http.post<AdminUserDetail>(`${this.users}/${id}/${action}`, body);
  }

  renameUser(id: string, displayName: string, reason: string | null): Observable<AdminUserDetail> {
    return this.http.put<AdminUserDetail>(`${this.users}/${id}/display-name`, {
      displayName,
      reason,
    });
  }
}
