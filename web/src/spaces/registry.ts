import { adminSpace } from './admin';
import { clashSpace } from './clash';
import { exportSpace } from './export';
import { familiesSpace } from './families';
import { farmSpace } from './farm';
import { loiSpace } from './loi';
import type { Space } from './types';

/** Все пространства в порядке полосы: модули, затем «Ферма» и «Администрирование». */
export const allSpaces: Space[] = [exportSpace, loiSpace, clashSpace, familiesSpace, farmSpace, adminSpace];
