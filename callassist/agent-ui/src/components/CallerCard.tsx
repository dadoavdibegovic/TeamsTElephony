import type { CallerInfo } from "../types/callTypes";

interface Props {
  callerInfo: CallerInfo | null;
  isLoading:  boolean;
}

const styles: Record<string, React.CSSProperties> = {
  card: {
    background:    "#1e293b",
    border:        "1px solid #334155",
    borderRadius:  10,
    padding:       20,
    color:         "#e2e8f0",
    fontFamily:    "system-ui, sans-serif",
    minWidth:      280,
  },
  header: {
    fontSize:    12,
    color:       "#94a3b8",
    letterSpacing: 1,
    textTransform: "uppercase" as const,
    marginBottom: 4,
  },
  phone: {
    fontSize:    20,
    fontWeight:  600,
    marginBottom: 12,
    color:       "#f1f5f9",
  },
  name: {
    fontSize:    16,
    fontWeight:  500,
    marginBottom: 4,
    color:       "#f1f5f9",
  },
  section: {
    marginTop:    16,
    paddingTop:   12,
    borderTop:    "1px solid #334155",
  },
  sectionTitle: {
    fontSize:      11,
    color:         "#64748b",
    textTransform: "uppercase" as const,
    letterSpacing: 1,
    marginBottom:  8,
  },
  row: {
    display:      "flex",
    justifyContent: "space-between",
    fontSize:     14,
    marginBottom: 4,
    gap:          12,
  },
  rowLabel: {
    color: "#94a3b8",
  },
  rowValue: {
    color:    "#f1f5f9",
    textAlign: "right" as const,
  },
  empty: {
    color:    "#64748b",
    fontSize: 13,
    fontStyle: "italic",
  },
  skeletonLine: {
    background:    "#334155",
    borderRadius:  4,
    height:        14,
    marginBottom:  8,
    animation:     "pulse 1.5s ease-in-out infinite",
  },
};

function Row({ label, value }: { label: string; value: string | null | undefined }) {
  if (!value) return null;
  return (
    <div style={styles.row}>
      <span style={styles.rowLabel}>{label}</span>
      <span style={styles.rowValue}>{value}</span>
    </div>
  );
}

export function CallerCard({ callerInfo, isLoading }: Props) {
  if (isLoading && !callerInfo) {
    return (
      <div style={styles.card}>
        <div style={styles.header}>Caller</div>
        <div style={{ ...styles.skeletonLine, width: "60%", height: 20 }} />
        <div style={{ ...styles.skeletonLine, width: "80%" }} />
        <div style={{ ...styles.skeletonLine, width: "70%" }} />
        <div style={{ ...styles.skeletonLine, width: "50%" }} />
      </div>
    );
  }

  if (!callerInfo) {
    return (
      <div style={styles.card}>
        <div style={styles.header}>Caller</div>
        <div style={styles.empty}>No active call</div>
      </div>
    );
  }

  const { phoneNumber, displayName, entra, crm } = callerInfo;

  return (
    <div style={styles.card}>
      <div style={styles.header}>Caller</div>
      <div style={styles.phone}>{phoneNumber}</div>
      {displayName && <div style={styles.name}>{displayName}</div>}

      {entra && (
        <div style={styles.section}>
          <div style={styles.sectionTitle}>Entra ID</div>
          <Row label="Email"      value={entra.mail} />
          <Row label="Job title"  value={entra.jobTitle} />
          <Row label="Department" value={entra.department} />
          <Row label="Company"    value={entra.companyName} />
          <Row label="Manager"    value={entra.manager} />
        </div>
      )}

      {crm && (
        <div style={styles.section}>
          <div style={styles.sectionTitle}>CRM</div>
          <Row label="Customer #"  value={crm.customerId} />
          <Row label="Account"     value={crm.accountName} />
          <Row label="Open tickets"
               value={crm.openTickets !== null ? String(crm.openTickets) : null} />
          <Row label="Last contact" value={crm.lastContactDate} />
          <Row label="Notes"        value={crm.notes} />
        </div>
      )}

      {!entra && !crm && (
        <div style={styles.section}>
          <div style={styles.empty}>No enrichment data available</div>
        </div>
      )}
    </div>
  );
}
