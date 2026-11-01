CREATE TABLE CUSTOMER_REFERENCE (
 ID BIGINT NOT NULL AUTO_INCREMENT COMMENT 'Transactions-local numeric primary key',
 UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Exact Core customer UUID; shared canonical customer identity',
 PROGRAM_UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Immutable Program scope from Core',
 USER_UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Immutable Identity Management user UUID from Core',
 STATUS VARCHAR(16) NOT NULL COMMENT 'ACTIVE or RESTRICTED customer snapshot; not an authorization grant',
 SOURCE_VERSION BIGINT NOT NULL COMMENT 'Highest applied Core customer version',
 LAST_EVENT_UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Last snapshot applied to this projection',
 CREATED_BY_UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Synchronization workload that created the projection',
 CREATED_DATETIME DATETIME(6) NOT NULL COMMENT 'UTC projection creation time',
 UPDATED_BY_UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Workload last updating the projection',
 UPDATED_DATETIME DATETIME(6) NOT NULL COMMENT 'UTC projection update time',
 ROW_VERSION BIGINT UNSIGNED NOT NULL DEFAULT 1 COMMENT 'Local projection concurrency version',
 PRIMARY KEY (ID), UNIQUE KEY UX_CUSTOMER_REFERENCE_UUID (UUID),
 KEY IX_CUSTOMER_REFERENCE_SCOPE (PROGRAM_UUID,USER_UUID),
 CONSTRAINT CK_CUSTOMER_REFERENCE_VERSION CHECK (SOURCE_VERSION>0),
 CONSTRAINT CK_CUSTOMER_REFERENCE_STATUS CHECK (STATUS IN ('ACTIVE','RESTRICTED'))
) ENGINE=InnoDB COMMENT='Minimal customer projection owned by Transactions; no cross-service database access';
CREATE TABLE CUSTOMER_EVENT_INBOX (
 ID BIGINT NOT NULL AUTO_INCREMENT COMMENT 'Transactions-local numeric primary key',
 UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Source event UUID for deduplication',
 CUSTOMER_UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Core customer UUID in the event',
 SOURCE_VERSION BIGINT NOT NULL COMMENT 'Source snapshot version',
 PAYLOAD_HASH BINARY(32) NOT NULL COMMENT 'SHA256 fingerprint to detect conflicting event UUID reuse',
 RESULT_CODE VARCHAR(16) NOT NULL COMMENT 'APPLIED or STALE; rejected messages have separate durable records',
 CREATED_DATETIME DATETIME(6) NOT NULL COMMENT 'UTC committed handling time',
 PRIMARY KEY (ID), UNIQUE KEY UX_CUSTOMER_INBOX_UUID (UUID),
 CONSTRAINT CK_CUSTOMER_INBOX_RESULT CHECK (RESULT_CODE IN ('APPLIED','STALE'))
) ENGINE=InnoDB COMMENT='Deduplication committed in the same transaction as customer projection and audit';
CREATE TABLE CUSTOMER_EVENT_REJECTIONS (
 ID BIGINT NOT NULL AUTO_INCREMENT COMMENT 'Transactions-local numeric primary key',
 UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Application-generated quarantine record UUID',
 TOPIC VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Trusted subscribed Kafka topic name',
 PARTITION_NUMBER INT NOT NULL COMMENT 'Kafka partition containing the rejected message',
 OFFSET_NUMBER BIGINT NOT NULL COMMENT 'Kafka offset durably quarantined before acknowledgement',
 PAYLOAD_HASH BINARY(32) NOT NULL COMMENT 'SHA256 of rejected bytes; raw content is never retained',
 ERROR_CODE VARCHAR(64) NOT NULL COMMENT 'Safe permanent validation or conflict classification',
 STATE VARCHAR(16) NOT NULL DEFAULT 'MANUAL_REVIEW' COMMENT 'Operator review required; raw payload is not exposed',
 CREATED_BY_UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Workload recording this rejection',
 CREATED_DATETIME DATETIME(6) NOT NULL COMMENT 'UTC rejection time',
 UPDATED_BY_UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Workload or operator last changing review state',
 UPDATED_DATETIME DATETIME(6) NOT NULL COMMENT 'UTC review state update time',
 ROW_VERSION BIGINT UNSIGNED NOT NULL DEFAULT 1 COMMENT 'Review state concurrency version',
 PRIMARY KEY (ID), UNIQUE KEY UX_CUSTOMER_REJECTION_UUID (UUID),
 UNIQUE KEY UX_CUSTOMER_REJECTION_POSITION (TOPIC,PARTITION_NUMBER,OFFSET_NUMBER)
) ENGINE=InnoDB COMMENT='Durable poison-message quarantine; offsets commit only after persistence';
