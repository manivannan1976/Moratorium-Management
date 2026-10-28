export type Role = 'Admin' | 'Viewer';
export type MoratoriumStatus = 'Draft' | 'Scheduled' | 'Active' | 'Inactive';

export interface UserInfo { email: string; displayName: string; role: Role; }
export interface LoginResponse { token: string; expiresAt: string; user: UserInfo; }

export interface Area {
  state: string | null;
  stateName?: string;
  statewide: boolean;
  counties: string[] | null;
  zipCodes: string[] | null;
}

export interface MoratoriumSummary {
  moratoriumId: string;
  name: string;
  status: MoratoriumStatus;
  timeZoneId: string;
  activationDateTime: string | null;
  deactivationDateTime: string | null;
  lastUpdatedDate: string;
  lastUpdatedBy: string;
  stateCount: number;
  countyCount: number;
  zipCount: number;
  detailPageUrl: string;
}

export interface MoratoriumDetail extends MoratoriumSummary {
  eventDescription: string;
  createdBy: string;
  createdDate: string;
  areas: Area[];
}

export interface MoratoriumRequest {
  name: string;
  timeZoneId: string;
  activationDateTime?: string | null;
  deactivationDateTime?: string | null;
  activateImmediately: boolean;
  saveAsDraft: boolean;
  areas: Area[];
}

export interface Paged<T> { items: T[]; total: number; }
export interface ActionResponse<T> { message: string; data: T; }

export interface FieldError { field?: string; errorCode?: string; message: string; }
export interface ApiError { errorCode: string; message: string; validationErrors?: FieldError[]; }

export interface AuditEntry {
  id: number;
  moratoriumId: string;
  moratoriumName: string;
  timestamp: string;
  user: string;
  action: string;
  detail?: string;
  previousValue?: string;
  newValue?: string;
}

export interface StateRef { code: string; name: string; }
