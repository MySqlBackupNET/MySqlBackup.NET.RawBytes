/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!40101 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;


-- 
-- Definition of binary_data
-- 

DROP TABLE IF EXISTS `binary_data`;
CREATE TABLE IF NOT EXISTS `binary_data` (
  `id` int NOT NULL AUTO_INCREMENT,
  `payload` blob,
  `raw16` binary(16) DEFAULT NULL,
  `flags` bit(8) DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=6 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- 
-- Dumping data for table binary_data
-- 

INSERT INTO `binary_data` (`id`, `payload`, `raw16`, `flags`) VALUES (1,0xDEADBEEF,0x0102030405060708090A0B0C0D0E0F10,0xAA), (2,0x00,0xFF00FF00FF00FF00FF00FF00FF00FF00,0x01), (3,0x706C61696E20746578742073746F72656420696E20626C6F62,NULL,NULL), (4,NULL,NULL,NULL), (5,'',0x00000000000000000000000000000000,0xFF);


-- 
-- Definition of empty_table
-- 

DROP TABLE IF EXISTS `empty_table`;
CREATE TABLE IF NOT EXISTS `empty_table` (
  `id` int NOT NULL AUTO_INCREMENT,
  `note` varchar(50) DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- 
-- Dumping data for table empty_table
-- 



-- 
-- Definition of products
-- 

DROP TABLE IF EXISTS `products`;
CREATE TABLE IF NOT EXISTS `products` (
  `id` int NOT NULL AUTO_INCREMENT,
  `name` varchar(100) NOT NULL,
  `description` text,
  `price` decimal(10,2) NOT NULL,
  `weight` double DEFAULT NULL,
  `created_at` datetime DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=1000 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- 
-- Dumping data for table products
-- 

INSERT INTO `products` (`id`, `name`, `description`, `price`, `weight`, `created_at`) VALUES (1,'Simple Widget','A plain description.',9.99,1.5,'2026-01-15 10:30:00'), (2,'O''Brien''s Special','Has a single quote '' inside',19.95,NULL,'2026-02-20 14:00:00'), (3,'Back\\slash item','Path C:\\temp\\file.txt and a tab	here',5.00,0.25,NULL), (4,'Multi\nline','Line one\nLine two\nLine three',100.00,12.345,'2026-03-01 00:00:00'), (5,'Unicode 世界 🌍','Emoji test 😀 and accents café résumé',42.50,NULL,'2026-04-10 08:15:30'), (6,'NullDesc',NULL,0.01,NULL,NULL);


-- 
-- Definition of tags
-- 

DROP TABLE IF EXISTS `tags`;
CREATE TABLE IF NOT EXISTS `tags` (
  `label` varchar(40) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=latin1;


-- 
-- Dumping data for table tags
-- 

INSERT INTO `tags` (`label`) VALUES ('alpha'), ('beta'), ('gamma'), ('delta');


-- 
-- Definition of ts_data
-- 

DROP TABLE IF EXISTS `ts_data`;
CREATE TABLE IF NOT EXISTS `ts_data` (
  `id` int NOT NULL AUTO_INCREMENT,
  `label` varchar(40) NOT NULL,
  `ts` timestamp NOT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=4 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- 
-- Dumping data for table ts_data
-- 

INSERT INTO `ts_data` (`id`, `label`, `ts`) VALUES (1,'epoch+1','2026-06-04 04:00:00'), (2,'midnight','2025-12-31 16:00:00'), (3,'year-end','2026-12-31 15:59:59');


/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;
/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;
