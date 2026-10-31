-- Disposable C2 rehearsal only, against the last UUID account schema.
-- Fixed development persona and audit rows; no copied user data or usable credentials.
INSERT INTO identity.users
    (id,status,username,username_version,username_reviewed_at,username_changed_at,
     security_version,lifecycle_generation,created_at,updated_at,last_login_at)
VALUES ('019a0000-0000-7000-8000-000000000001','active','c2_listener',3,'2026-10-01',NULL,
    1,1,'2026-09-01','2026-10-01','2026-10-01');
INSERT INTO identity.external_identities
    (id,user_id,issuer,provider_subject,last_login_at,created_at,updated_at)
VALUES ('019a0000-0000-7000-8000-000000000002','019a0000-0000-7000-8000-000000000001',
    'https://accounts.google.com','dev-google-alice','2026-10-01','2026-09-01','2026-10-01');
INSERT INTO identity.openiddict_authorizations (id,status,subject,type)
VALUES ('019a0000-0000-7000-8000-000000000003','valid','019a0000-0000-7000-8000-000000000001','permanent');
INSERT INTO identity.openiddict_tokens (id,authorization_id,status,subject,type,payload)
VALUES ('019a0000-0000-7000-8000-000000000004','019a0000-0000-7000-8000-000000000003',
    'valid','019a0000-0000-7000-8000-000000000001','refresh_token','opaque-pre-cutover-payload');
INSERT INTO identity.native_sessions
    (id,user_id,authorization_id,client_id,security_version,authenticated_at,last_used_at,
     absolute_expires_at,created_at,updated_at)
VALUES ('019a0000-0000-7000-8000-000000000005','019a0000-0000-7000-8000-000000000001',
    '019a0000-0000-7000-8000-000000000003','relisten-mobile-ios-dev',1,
    '2026-10-01','2026-10-01','2099-01-01','2026-10-01','2026-10-01');
INSERT INTO identity.sessions
    (id,user_id,purpose,validator_hash,security_version,authenticated_at,created_at,updated_at,
     last_seen_at,sliding_expires_at,absolute_expires_at,auth_sso_session_id,web_origin,capabilities)
VALUES
    ('019a0000-0000-7000-8000-000000000006','019a0000-0000-7000-8000-000000000001',
     'auth_sso',decode(repeat('ab',32),'hex'),1,'2026-10-01','2026-10-01','2026-10-01',
     '2026-10-01','2099-01-01','2099-01-01',NULL,NULL,0),
    ('019a0000-0000-7000-8000-000000000007','019a0000-0000-7000-8000-000000000001',
     'web',decode(repeat('cd',32),'hex'),1,'2026-10-01','2026-10-01','2026-10-01',
     '2026-10-01','2099-01-01','2099-01-01','019a0000-0000-7000-8000-000000000006',
     'https://web.relisten.localhost:5173',7);
INSERT INTO identity.username_holds (id,user_id,username,release_at,created_at)
VALUES ('019a0000-0000-7000-8000-000000000008','019a0000-0000-7000-8000-000000000001',
    'previous_listener','2099-01-01','2026-10-01');
INSERT INTO identity.username_command_receipts (id,user_id,expected_username_version,payload_hash,stored_result,created_at)
VALUES ('019a0000-0000-7000-8000-000000000009','019a0000-0000-7000-8000-000000000001',2,
    decode(repeat('12',32),'hex'),'{"Username":"c2_listener","UsernameVersion":3,"UsernameReviewedAt":"2026-10-01T00:00:00+00:00","UsernameChangedAt":null}', '2026-10-01');
INSERT INTO user_data.library_states (id,user_id,revision,created_at,updated_at)
VALUES ('019a0000-0000-7000-8000-000000000010','019a0000-0000-7000-8000-000000000001',4,'2026-09-01','2026-10-01');
-- Cover every old kind, including the three whose vocabulary changes at cutover.
INSERT INTO user_data.favorites (id,user_id,catalog_type,catalog_uuid,created_at,updated_at)
SELECT ('019a0000-0000-7000-8000-' || lpad(n::text,12,'0'))::uuid,
    '019a0000-0000-7000-8000-000000000001', kind,
    '777f627f-442e-9a6f-a366-ac734827dcc5','2026-09-01','2026-10-01'
FROM (VALUES (101,'artist'),(102,'show'),(103,'source'),(104,'source_track'),
    (105,'song'),(106,'tour'),(107,'venue')) AS kinds(n,kind);
INSERT INTO user_data.favorite_mutation_receipts
    (id,user_id,catalog_type,catalog_uuid,desired_state,submitted_favorite_id,payload_hash,
     changed,canonical_favorite_id,library_revision,created_at)
VALUES ('019a0000-0000-7000-8000-000000000201','019a0000-0000-7000-8000-000000000001',
    'venue','777f627f-442e-9a6f-a366-ac734827dcc5','favorite','019a0000-0000-7000-8000-000000000107',
    decode(repeat('34',32),'hex'),true,'019a0000-0000-7000-8000-000000000107',4,'2026-10-01');
INSERT INTO user_data.library_changes (id,user_id,revision,change_type,favorite_id,catalog_type,catalog_uuid,changed_at)
VALUES ('019a0000-0000-7000-8000-000000000301','019a0000-0000-7000-8000-000000000001',4,
    'favorite_added','019a0000-0000-7000-8000-000000000107','venue',
    '777f627f-442e-9a6f-a366-ac734827dcc5','2026-10-01');
