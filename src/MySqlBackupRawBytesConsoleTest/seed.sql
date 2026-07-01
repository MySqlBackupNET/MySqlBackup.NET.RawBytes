-- Test database seed for MySqlBackup.NET.RawBytes new library
-- Exercises every cell-emission path: numeric, quoted (with special chars/unicode),
-- binary/hex, NULL, plus table-option stripping (AUTO_INCREMENT=, DEFAULT CHARSET=).

DROP DATABASE IF EXISTS rawbytes_test;
CREATE DATABASE rawbytes_test CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
USE rawbytes_test;

-- 1) Mixed types: numeric + quoted strings + NULL + datetime
CREATE TABLE products (
    id          INT NOT NULL AUTO_INCREMENT,
    name        VARCHAR(100) NOT NULL,
    description TEXT NULL,
    price       DECIMAL(10,2) NOT NULL,
    weight      DOUBLE NULL,
    created_at  DATETIME NULL,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO products (name, description, price, weight, created_at) VALUES
('Simple Widget', 'A plain description.', 9.99, 1.5, '2026-01-15 10:30:00'),
('O''Brien''s Special', 'Has a single quote '' inside', 19.95, NULL, '2026-02-20 14:00:00'),
('Back\\slash item', 'Path C:\\temp\\file.txt and a tab\there', 5.00, 0.25, NULL),
('Multi\nline', 'Line one\nLine two\nLine three', 100.00, 12.345, '2026-03-01 00:00:00'),
('Unicode 世界 🌍', 'Emoji test 😀 and accents café résumé', 42.50, NULL, '2026-04-10 08:15:30'),
('NullDesc', NULL, 0.01, NULL, NULL);

-- bump auto_increment so AUTO_INCREMENT= appears in SHOW CREATE TABLE
ALTER TABLE products AUTO_INCREMENT = 1000;

-- 2) Binary / hex emission paths
CREATE TABLE binary_data (
    id      INT NOT NULL AUTO_INCREMENT,
    payload BLOB NULL,
    raw16   BINARY(16) NULL,
    flags   BIT(8) NULL,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO binary_data (payload, raw16, flags) VALUES
(0xDEADBEEF, 0x0102030405060708090A0B0C0D0E0F10, b'10101010'),
(0x00, UNHEX('FF00FF00FF00FF00FF00FF00FF00FF00'), b'00000001'),
('plain text stored in blob', NULL, NULL),
(NULL, NULL, NULL),
('', 0x00000000000000000000000000000000, b'11111111');

-- 3) Empty table (must produce CREATE TABLE but NO INSERT)
CREATE TABLE empty_table (
    id   INT NOT NULL AUTO_INCREMENT,
    note VARCHAR(50) NULL,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 4) Single-column table (formatting of single-cell rows)
CREATE TABLE tags (
    label VARCHAR(40) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

INSERT INTO tags (label) VALUES ('alpha'),('beta'),('gamma'),('delta');

-- 5) TIMESTAMP table (exercises the UTC session-timezone export path)
CREATE TABLE ts_data (
    id    INT NOT NULL AUTO_INCREMENT,
    label VARCHAR(40) NOT NULL,
    ts    TIMESTAMP NOT NULL,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Insert known instants. Values are interpreted in the session time zone at insert.
INSERT INTO ts_data (label, ts) VALUES
('epoch+1',   '2026-06-04 12:00:00'),
('midnight',  '2026-01-01 00:00:00'),
('year-end',  '2026-12-31 23:59:59');
