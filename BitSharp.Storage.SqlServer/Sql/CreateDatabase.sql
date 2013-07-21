CREATE TABLE Blocks
(
	BlockHash CHAR(32) CHARACTER SET OCTETS NOT NULL,
	RawBytes BLOB SUB_TYPE BINARY NOT NULL,
	CONSTRAINT PK_Blocks PRIMARY KEY
	(
		BlockHash
	)
);

CREATE TABLE ChainedBlocks
(
	BlockHash CHAR(32) CHARACTER SET OCTETS NOT NULL,
	PreviousBlockHash CHAR(32) CHARACTER SET OCTETS NOT NULL,
	"Work" CHAR(64) CHARACTER SET OCTETS NOT NULL,
	Height BIGINT,
	TotalWork CHAR(64) CHARACTER SET OCTETS,
	IsValid INTEGER,
	CONSTRAINT PK_ChainedBlocks PRIMARY KEY
	(
		BlockHash
	)
);

CREATE INDEX IX_ChainedBlocks_PrevBlockHash ON ChainedBlock ( PreviousBlockHash );

CREATE INDEX IX_ChainedBlocks_Height ON ChainedBlocks ( Height );

CREATE INDEX IX_ChainedBlocks_TotalWork ON ChainedBlocks ( TotalWork );

CREATE TABLE KnownAddresses
(
	IPAddress CHAR(16) CHARACTER SET OCTETS NOT NULL,
	Port CHAR(2) CHARACTER SET OCTETS NOT NULL,
	Services CHAR(8) CHARACTER SET OCTETS NOT NULL,
	"Time" CHAR(4) CHARACTER SET OCTETS NOT NULL,
	CONSTRAINT PK_KnownAddresses PRIMARY KEY
	(
		IPAddress,
		Port
	)
);

CREATE TABLE TransactionLocators
(
	BlockHash CHAR(32) CHARACTER SET OCTETS NOT NULL,
	TransactionHash CHAR(32) CHARACTER SET OCTETS NOT NULL,
	TransactionIndex CHAR(4) CHARACTER SET OCTETS NOT NULL,
	CONSTRAINT PK_TransactionLocators PRIMARY KEY
	(
		BlockHash,
		TransactionHash
	)
);
