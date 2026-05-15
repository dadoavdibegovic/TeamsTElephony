export interface CallerInfo {
  phoneNumber:  string;
  displayName:  string | null;
  entra:        EntraProfile | null;
  crm:          CrmProfile | null;
  enrichedAt:   Date;
}

export interface EntraProfile {
  id:          string;
  displayName: string;
  mail:        string | null;
  jobTitle:    string | null;
  department:  string | null;
  companyName: string | null;
  manager:     string | null;
}

export interface CrmProfile {
  customerId:      string | null;
  fullName:        string | null;
  accountName:     string | null;
  openTickets:     number | null;
  lastContactDate: string | null;
  notes:           string | null;
  rawData:         Record<string, unknown>;
}