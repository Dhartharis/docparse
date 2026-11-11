CREATE SCHEMA billing;

CREATE TABLE billing.accounts (
    organization_id uuid PRIMARY KEY REFERENCES access.organizations("Id"),
    customer_id text UNIQUE,
    subscription_id text UNIQUE,
    subscription_status text NOT NULL DEFAULT 'none',
    plan_key text,
    updated_at timestamptz NOT NULL DEFAULT now(),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE billing.checkout_attempts (
    id uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES billing.accounts(organization_id),
    request_id uuid NOT NULL,
    plan_key text NOT NULL CHECK (plan_key IN ('starter', 'pro')),
    price_id text NOT NULL,
    return_base text NOT NULL,
    state text NOT NULL CHECK (state IN ('creating', 'pending', 'complete', 'expired', 'failed')),
    checkout_id text UNIQUE,
    expires_at timestamptz,
    lease_id uuid,
    lease_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, request_id)
);
CREATE UNIQUE INDEX billing_one_open_checkout ON billing.checkout_attempts(organization_id)
    WHERE state IN ('creating', 'pending');

CREATE TABLE billing.webhook_inbox (
    event_id text PRIMARY KEY,
    customer_id text NOT NULL,
    event_type text NOT NULL,
    state text NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'processing', 'complete', 'ignored', 'failed')),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    due_at timestamptz NOT NULL DEFAULT now(),
    lease_id uuid,
    lease_until timestamptz,
    error_code text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX billing_webhooks_due ON billing.webhook_inbox(state, due_at, lease_until);
