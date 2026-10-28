import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import {
  ActionResponse, ApiError, AuditEntry, MoratoriumDetail, MoratoriumRequest, MoratoriumSummary, Paged, StateRef,
} from './models';

export function toApiError(e: unknown): ApiError {
  const err = e as HttpErrorResponse;
  const body = err?.error as ApiError | undefined;
  if (body && typeof body === 'object' && 'errorCode' in body) return body;
  if (err?.status === 0) return { errorCode: 'NETWORK', message: 'Cannot reach the server. Please try again.' };
  return { errorCode: 'SYSTEM_ERROR', message: 'An unexpected system error occurred. Please try again later.' };
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);
  private base = '/api';

  list(status?: string, search?: string, skip = 0, take = 10) {
    let params = new HttpParams().set('skip', skip).set('take', take);
    if (status) params = params.set('status', status);
    if (search) params = params.set('search', search);
    return this.http.get<Paged<MoratoriumSummary>>(`${this.base}/moratoriums`, { params });
  }
  get(id: string) { return this.http.get<MoratoriumDetail>(`${this.base}/moratoriums/${id}`); }
  create(req: MoratoriumRequest) { return this.http.post<ActionResponse<MoratoriumDetail>>(`${this.base}/moratoriums`, req); }
  update(id: string, req: MoratoriumRequest) { return this.http.put<ActionResponse<MoratoriumDetail>>(`${this.base}/moratoriums/${id}`, req); }
  activate(id: string) { return this.http.post<ActionResponse<MoratoriumDetail>>(`${this.base}/moratoriums/${id}/activate`, {}); }
  deactivate(id: string) { return this.http.post<ActionResponse<MoratoriumDetail>>(`${this.base}/moratoriums/${id}/deactivate`, {}); }
  audit(id: string | null, skip = 0, take = 50) {
    const params = new HttpParams().set('skip', skip).set('take', take);
    const url = id ? `${this.base}/moratoriums/${id}/audit` : `${this.base}/audit`;
    return this.http.get<Paged<AuditEntry>>(url, { params });
  }
  states() { return this.http.get<Paged<StateRef>>(`${this.base}/reference/states`); }
}
