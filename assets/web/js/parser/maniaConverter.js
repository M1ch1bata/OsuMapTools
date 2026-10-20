// Port of osu!lazer's legacy mania beatmap conversion
// (osu.Game.Rulesets.Mania/Beatmaps/...Patterns/Legacy + Utils/LegacyRandom),
// which is the implementation written to match osu!stable's convert behaviour.
//
// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See https://github.com/ppy/osu/blob/master/LICENCE for the original licence text.
//
// Input: parsed legacy (.osu) hit objects for a non-mania ruleset, plus timing
// points, breaks and difficulty values. Output: mania notes/holds in stable
// coordinate behaviour (columns / start / end).

const HIT_NORMAL = "Normal";
const HIT_WHISTLE = "Whistle";
const HIT_FINISH = "Finish";
const HIT_CLAP = "Clap";

export const PatternType = Object.freeze({
    None: 0,
    ForceStack: 1,
    ForceNotStack: 1 << 1,
    KeepSingle: 1 << 2,
    LowProbability: 1 << 3,
    Alternate: 1 << 4,
    ForceSigSlider: 1 << 5,
    ForceNotSlider: 1 << 6,
    Gathered: 1 << 7,
    Mirror: 1 << 8,
    Reverse: 1 << 9,
    Cycle: 1 << 10,
    Stair: 1 << 11,
    ReverseStair: 1 << 12,
});

function hasFlag(value, flag) {
    return (value & flag) !== 0;
}

function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
}

function roundHalfEven(value) {
    const floor = Math.floor(value);
    const diff = value - floor;
    if (diff > 0.5) return floor + 1;
    if (diff < 0.5) return floor;
    return floor % 2 === 0 ? floor : floor + 1;
}

function hasClap(samples) {
    return samples.has(HIT_CLAP);
}

function hasFinish(samples) {
    return samples.has(HIT_FINISH);
}

function isDoubleSample(samples) {
    return samples.has(HIT_CLAP) || samples.has(HIT_FINISH);
}

export class LegacyRandom {
    constructor(seed) {
        this.X = seed >>> 0;
        this.Y = 842502087;
        this.Z = 3579807591;
        this.W = 273326509;
        this.bitBuffer = 0;
        this.bitIndex = 32;
    }

    nextUInt() {
        const t = (this.X ^ (this.X << 11)) >>> 0;
        this.X = this.Y;
        this.Y = this.Z;
        this.Z = this.W;
        this.W = (this.W ^ (this.W >>> 19) ^ t ^ (t >>> 8)) >>> 0;
        return this.W;
    }

    next() {
        return this.nextUInt() & 0x7fffffff;
    }

    nextDouble() {
        return (1.0 / 2147483648.0) * this.next();
    }

    nextInt(upperBound) {
        return Math.trunc(this.nextDouble() * upperBound);
    }

    nextIntRange(lowerBound, upperBound) {
        return Math.trunc(lowerBound + this.nextDouble() * (upperBound - lowerBound));
    }
}

export class Pattern {
    constructor() {
        this.objects = [];
        this.containedColumns = new Set();
    }

    get hitObjects() {
        return this.objects;
    }

    get columnWithObjects() {
        return this.containedColumns.size;
    }

    columnHasObject(column) {
        return this.containedColumns.has(column);
    }

    add(object) {
        this.objects.push(object);
        this.containedColumns.add(object.column);
    }

    addPattern(other) {
        for (const object of other.objects) this.add(object);
    }

    clear() {
        this.objects.length = 0;
        this.containedColumns.clear();
    }
}

class NotEnoughColumnsException extends Error {
    constructor() {
        super("There were not enough columns to complete conversion.");
        this.name = "NotEnoughColumnsException";
    }
}

class LegacyPatternGenerator {
    constructor(random, hitObject, beatmap, previousPattern, totalColumns) {
        this.Random = random;
        this.HitObject = hitObject;
        this.Beatmap = beatmap;
        this.PreviousPattern = previousPattern;
        this.TotalColumns = totalColumns;
        this.RandomStart = totalColumns === 8 ? 1 : 0;
        this._conversionDifficulty = null;
    }

    get hitObjectSamples() {
        return this.HitObject.samples;
    }

    get conversionDifficulty() {
        if (this._conversionDifficulty !== null) return this._conversionDifficulty;
        const objects = this.Beatmap.hitObjects;
        const lastObject = objects.length ? objects[objects.length - 1] : null;
        const firstObject = objects.length ? objects[0] : null;
        let drainTime = Math.trunc(
            (((lastObject ? lastObject.startTime : 0) - (firstObject ? firstObject.startTime : 0)) - this.Beatmap.totalBreakTime) / 1000,
        );
        if (drainTime === 0) drainTime = 10000;
        const difficulty = this.Beatmap.difficulty;
        let value = ((difficulty.drainRate + clamp(difficulty.approachRate, 4, 7)) / 1.5
            + (objects.length / drainTime) * 9) / 38 * 5 / 1.15;
        value = Math.min(value, 12);
        this._conversionDifficulty = value;
        return value;
    }

    getColumn(position, allowSpecial = false) {
        if (allowSpecial && this.TotalColumns === 8) {
            const localXDivisor = 512 / 7;
            return clamp(Math.floor(position / localXDivisor), 0, 6) + 1;
        }
        const localXDivisor = 512 / this.TotalColumns;
        return clamp(Math.floor(position / localXDivisor), 0, this.TotalColumns - 1);
    }

    getRandomNoteCount(p2, p3, p4 = 0, p5 = 0, p6 = 0) {
        const value = this.Random.nextDouble();
        if (value >= 1 - p6) return 6;
        if (value >= 1 - p5) return 5;
        if (value >= 1 - p4) return 4;
        if (value >= 1 - p3) return 3;
        return value >= 1 - p2 ? 2 : 1;
    }

    getRandomColumn(lowerBound = null, upperBound = null) {
        const lower = lowerBound === null ? this.RandomStart : lowerBound;
        const upper = upperBound === null ? this.TotalColumns : upperBound;
        return this.Random.nextIntRange(lower, upper);
    }

    findAvailableColumn(initialColumn, options = {}) {
        const lowerBound = options.lowerBound === undefined ? this.RandomStart : options.lowerBound;
        const upperBound = options.upperBound === undefined ? this.TotalColumns : options.upperBound;
        const nextColumn = options.nextColumn || (() => this.getRandomColumn(lowerBound, upperBound));
        const validation = options.validation || null;
        const patterns = options.patterns || [];

        const isValid = (column) => {
            if (validation && !validation(column)) return false;
            for (const pattern of patterns) {
                if (pattern.columnHasObject(column)) return false;
            }
            return true;
        };

        if (isValid(initialColumn)) return initialColumn;

        let hasValidColumns = false;
        for (let i = lowerBound; i < upperBound; i += 1) {
            if (isValid(i)) {
                hasValidColumns = true;
                break;
            }
        }
        if (!hasValidColumns) throw new NotEnoughColumnsException();

        let column = initialColumn;
        do {
            column = nextColumn(column);
        } while (!isValid(column));
        return column;
    }
}

class HitCirclePatternGenerator extends LegacyPatternGenerator {
    constructor(random, hitObject, beatmap, totalColumns, previousPattern, previousTime, previousPosition, density, lastStair) {
        super(random, hitObject, beatmap, previousPattern, totalColumns);
        this.StairType = lastStair;
        this.convertType = PatternType.None;

        const timingPoint = beatmap.beatLengthAt(hitObject.startTime);
        const kiaiMode = beatmap.kiaiAt(hitObject.startTime);
        const positionSeparation = Math.hypot(hitObject.x - previousPosition.x, hitObject.y - previousPosition.y);
        const timeSeparation = hitObject.startTime - previousTime;
        const samples = hitObject.samples;

        if (timeSeparation <= 80) {
            this.convertType |= PatternType.ForceNotStack | PatternType.KeepSingle;
        } else if (timeSeparation <= 95) {
            this.convertType |= PatternType.ForceNotStack | PatternType.KeepSingle | lastStair;
        } else if (timeSeparation <= 105) {
            this.convertType |= PatternType.ForceNotStack | PatternType.LowProbability;
        } else if (timeSeparation <= 125) {
            this.convertType |= PatternType.ForceNotStack;
        } else if (timeSeparation <= 135 && positionSeparation < 20) {
            this.convertType |= PatternType.Cycle | PatternType.KeepSingle;
        } else if (timeSeparation <= 150 && positionSeparation < 20) {
            this.convertType |= PatternType.ForceStack | PatternType.LowProbability;
        } else if (positionSeparation < 20 && density >= timingPoint / 2.5) {
            this.convertType |= PatternType.Reverse | PatternType.LowProbability;
        } else if (density < timingPoint / 2.5 || kiaiMode) {
            // High density: no extra flags.
        } else {
            this.convertType |= PatternType.LowProbability;
        }

        if (!hasFlag(this.convertType, PatternType.KeepSingle)) {
            if (hasFinish(samples) && totalColumns !== 8) this.convertType |= PatternType.Mirror;
            else if (hasClap(samples)) this.convertType |= PatternType.Gathered;
        }
    }

    generate() {
        const pattern = this.generateCore();
        for (const object of pattern.objects) {
            if (hasFlag(this.convertType, PatternType.Stair) && object.column === this.TotalColumns - 1) {
                this.StairType = PatternType.ReverseStair;
            }
            if (hasFlag(this.convertType, PatternType.ReverseStair) && object.column === this.RandomStart) {
                this.StairType = PatternType.Stair;
            }
        }
        return [pattern];
    }

    generateCore() {
        const pattern = new Pattern();
        if (this.TotalColumns === 1) {
            this.addToPattern(pattern, 0);
            return pattern;
        }

        const lastColumn = this.PreviousPattern.objects.length ? this.PreviousPattern.objects[0].column : 0;

        if (hasFlag(this.convertType, PatternType.Reverse) && this.PreviousPattern.objects.length) {
            for (let i = this.RandomStart; i < this.TotalColumns; i += 1) {
                if (this.PreviousPattern.columnHasObject(i)) {
                    this.addToPattern(pattern, this.RandomStart + this.TotalColumns - i - 1);
                }
            }
            return pattern;
        }

        if (hasFlag(this.convertType, PatternType.Cycle)
            && this.PreviousPattern.objects.length === 1
            && (this.TotalColumns !== 8 || lastColumn !== 0)
            && (this.TotalColumns % 2 === 0 || lastColumn !== this.TotalColumns / 2)) {
            const column = this.RandomStart + this.TotalColumns - lastColumn - 1;
            this.addToPattern(pattern, column);
            return pattern;
        }

        if (hasFlag(this.convertType, PatternType.ForceStack) && this.PreviousPattern.objects.length) {
            for (let i = this.RandomStart; i < this.TotalColumns; i += 1) {
                if (this.PreviousPattern.columnHasObject(i)) this.addToPattern(pattern, i);
            }
            return pattern;
        }

        if (this.PreviousPattern.objects.length === 1) {
            if (hasFlag(this.convertType, PatternType.Stair)) {
                let targetColumn = lastColumn + 1;
                if (targetColumn === this.TotalColumns) targetColumn = this.RandomStart;
                this.addToPattern(pattern, targetColumn);
                return pattern;
            }
            if (hasFlag(this.convertType, PatternType.ReverseStair)) {
                let targetColumn = lastColumn - 1;
                if (targetColumn === this.RandomStart - 1) targetColumn = this.TotalColumns - 1;
                this.addToPattern(pattern, targetColumn);
                return pattern;
            }
        }

        if (hasFlag(this.convertType, PatternType.KeepSingle)) return this.generateRandomNotes(1);

        if (hasFlag(this.convertType, PatternType.Mirror)) {
            if (this.conversionDifficulty > 6.5) return this.generateRandomPatternWithMirrored(0.12, 0.38, 0.12);
            if (this.conversionDifficulty > 4) return this.generateRandomPatternWithMirrored(0.12, 0.17, 0);
            return this.generateRandomPatternWithMirrored(0.12, 0, 0);
        }

        if (this.conversionDifficulty > 6.5) {
            if (hasFlag(this.convertType, PatternType.LowProbability)) return this.generateRandomPattern(0.78, 0.42, 0, 0);
            return this.generateRandomPattern(1, 0.62, 0, 0);
        }
        if (this.conversionDifficulty > 4) {
            if (hasFlag(this.convertType, PatternType.LowProbability)) return this.generateRandomPattern(0.35, 0.08, 0, 0);
            return this.generateRandomPattern(0.52, 0.15, 0, 0);
        }
        if (this.conversionDifficulty > 2) {
            if (hasFlag(this.convertType, PatternType.LowProbability)) return this.generateRandomPattern(0.18, 0, 0, 0);
            return this.generateRandomPattern(0.45, 0, 0, 0);
        }
        return this.generateRandomPattern(0, 0, 0, 0);
    }

    generateRandomNotes(noteCount) {
        const pattern = new Pattern();
        const allowStacking = !hasFlag(this.convertType, PatternType.ForceNotStack);
        if (!allowStacking) {
            noteCount = Math.min(noteCount, this.TotalColumns - this.RandomStart - this.PreviousPattern.columnWithObjects);
        }
        let nextColumn = this.getColumn(this.HitObject.x, true);
        const getNextColumn = (last) => {
            if (hasFlag(this.convertType, PatternType.Gathered)) {
                let next = last + 1;
                if (next === this.TotalColumns) next = this.RandomStart;
                return next;
            }
            return this.getRandomColumn();
        };
        for (let i = 0; i < noteCount; i += 1) {
            nextColumn = this.findAvailableColumn(nextColumn, {
                nextColumn: getNextColumn,
                patterns: allowStacking ? [pattern] : [pattern, this.PreviousPattern],
            });
            this.addToPattern(pattern, nextColumn);
        }
        return pattern;
    }

    get hasSpecialColumn() {
        const samples = this.HitObject.samples;
        return hasClap(samples) && hasFinish(samples);
    }

    generateRandomPattern(p2, p3, p4, p5) {
        const pattern = new Pattern();
        pattern.addPattern(this.generateRandomNotes(this.getRandomNoteCountForHitCircle(p2, p3, p4, p5)));
        if (this.RandomStart > 0 && this.hasSpecialColumn) this.addToPattern(pattern, 0);
        return pattern;
    }

    generateRandomPatternWithMirrored(centreProbability, p2, p3) {
        if (hasFlag(this.convertType, PatternType.ForceNotStack)) {
            return this.generateRandomPattern(1 / 2 + p2 / 2, p2, (p2 + p3) / 2, p3);
        }
        const pattern = new Pattern();
        const mirrored = this.getRandomNoteCountMirrored(centreProbability, p2, p3);
        const noteCount = mirrored.noteCount;
        const addToCentre = mirrored.addToCentre;
        const columnLimit = (this.TotalColumns % 2 === 0 ? this.TotalColumns : this.TotalColumns - 1) / 2;
        let nextColumn = this.getRandomColumn(undefined, columnLimit);
        for (let i = 0; i < noteCount; i += 1) {
            nextColumn = this.findAvailableColumn(nextColumn, { upperBound: columnLimit, patterns: [pattern] });
            this.addToPattern(pattern, nextColumn);
            this.addToPattern(pattern, this.RandomStart + this.TotalColumns - nextColumn - 1);
        }
        if (addToCentre) this.addToPattern(pattern, this.TotalColumns / 2);
        if (this.RandomStart > 0 && this.hasSpecialColumn) this.addToPattern(pattern, 0);
        return pattern;
    }

    getRandomNoteCountForHitCircle(p2, p3, p4, p5) {
        switch (this.TotalColumns) {
            case 2:
                p2 = 0; p3 = 0; p4 = 0; p5 = 0;
                break;
            case 3:
                p2 = Math.min(p2, 0.1); p3 = 0; p4 = 0; p5 = 0;
                break;
            case 4:
                p2 = Math.min(p2, 0.23); p3 = Math.min(p3, 0.04); p4 = 0; p5 = 0;
                break;
            case 5:
                p3 = Math.min(p3, 0.15); p4 = Math.min(p4, 0.03); p5 = 0;
                break;
            default:
                break;
        }
        if (hasClap(this.HitObject.samples)) p2 = 1;
        return this.getRandomNoteCount(p2, p3, p4, p5);
    }

    getRandomNoteCountMirrored(centreProbability, p2, p3) {
        switch (this.TotalColumns) {
            case 2:
                centreProbability = 0; p2 = 0; p3 = 0;
                break;
            case 3:
                centreProbability = Math.min(centreProbability, 0.03); p2 = 0; p3 = 0;
                break;
            case 4:
                centreProbability = 0;
                p2 = 1 - Math.max((1 - p2) * 2, 0.8);
                p3 = 0;
                break;
            case 5:
                centreProbability = Math.min(centreProbability, 0.03);
                p3 = 0;
                break;
            case 6:
                centreProbability = 0;
                p2 = 1 - Math.max((1 - p2) * 2, 0.5);
                p3 = 1 - Math.max((1 - p3) * 2, 0.85);
                break;
            default:
                break;
        }
        p2 = clamp(p2, 0, 1);
        p3 = clamp(p3, 0, 1);
        const centreVal = this.Random.nextDouble();
        const noteCount = this.getRandomNoteCount(p2, p3);
        const addToCentre = this.TotalColumns % 2 !== 0 && noteCount !== 3 && centreVal > 1 - centreProbability;
        return { noteCount, addToCentre };
    }

    addToPattern(pattern, column) {
        pattern.add({
            column,
            startTime: this.HitObject.startTime,
            endTime: this.HitObject.startTime,
            samples: this.HitObject.samples,
        });
    }
}

class SliderPatternGenerator extends LegacyPatternGenerator {
    constructor(random, hitObject, beatmap, totalColumns, previousPattern) {
        super(random, hitObject, beatmap, previousPattern, totalColumns);
        this.convertType = beatmap.kiaiAt(hitObject.startTime) ? PatternType.None : PatternType.LowProbability;

        let beatLength;
        if (hitObject.hasSliderVelocity) {
            beatLength = beatmap.precisionAdjustedBeatLength(hitObject.startTime, hitObject.sv);
        } else {
            beatLength = beatmap.beatLengthAt(hitObject.startTime);
        }

        this.SpanCount = hitObject.repeats || 1;
        this.StartTime = Math.round(hitObject.startTime);

        const distance = hitObject.pixelLength || 0;
        this.EndTime = Math.floor(this.StartTime + distance * beatLength * this.SpanCount * 0.01 / beatmap.difficulty.sliderMultiplier);
        this.SegmentDuration = Math.trunc((this.EndTime - this.StartTime) / this.SpanCount);
        this.nodeSamples = hitObject.nodeSamples || null;
    }

    generate() {
        const originalPattern = this.generatePattern();
        if (originalPattern.objects.length === 1) return [originalPattern];

        const intermediatePattern = new Pattern();
        const endTimePattern = new Pattern();
        for (const object of originalPattern.objects) {
            const endTime = object.startTime + (object.endTime - object.startTime);
            if (this.EndTime !== Math.round(endTime)) intermediatePattern.add(object);
            else endTimePattern.add(object);
        }
        return [intermediatePattern, endTimePattern];
    }

    generatePattern() {
        if (this.TotalColumns === 1) {
            const pattern = new Pattern();
            this.addToPattern(pattern, 0, this.StartTime, this.EndTime);
            return pattern;
        }

        if (this.SpanCount > 1) {
            if (this.SegmentDuration <= 90) return this.generateRandomHoldNotes(this.StartTime, 1);
            if (this.SegmentDuration <= 120) {
                this.convertType |= PatternType.ForceNotStack;
                return this.generateRandomNotes(this.StartTime, this.SpanCount + 1);
            }
            if (this.SegmentDuration <= 160) return this.generateStair(this.StartTime);
            if (this.SegmentDuration <= 200 && this.conversionDifficulty > 3) return this.generateRandomMultipleNotes(this.StartTime);

            const duration = this.EndTime - this.StartTime;
            if (duration >= 4000) return this.generateNRandomNotes(this.StartTime, 0.23, 0, 0);
            if (this.SegmentDuration > 400 && this.SpanCount < this.TotalColumns - 1 - this.RandomStart) {
                return this.generateTiledHoldNotes(this.StartTime);
            }
            return this.generateHoldAndNormalNotes(this.StartTime);
        }

        if (this.SegmentDuration <= 110) {
            if (this.PreviousPattern.columnWithObjects < this.TotalColumns) {
                this.convertType |= PatternType.ForceNotStack;
            } else {
                this.convertType &= ~PatternType.ForceNotStack;
            }
            return this.generateRandomNotes(this.StartTime, this.SegmentDuration < 80 ? 1 : 2);
        }

        if (this.conversionDifficulty > 6.5) {
            if (hasFlag(this.convertType, PatternType.LowProbability)) return this.generateNRandomNotes(this.StartTime, 0.78, 0.3, 0);
            return this.generateNRandomNotes(this.StartTime, 0.85, 0.36, 0.03);
        }
        if (this.conversionDifficulty > 4) {
            if (hasFlag(this.convertType, PatternType.LowProbability)) return this.generateNRandomNotes(this.StartTime, 0.43, 0.08, 0);
            return this.generateNRandomNotes(this.StartTime, 0.56, 0.18, 0);
        }
        if (this.conversionDifficulty > 2.5) {
            if (hasFlag(this.convertType, PatternType.LowProbability)) return this.generateNRandomNotes(this.StartTime, 0.3, 0, 0);
            return this.generateNRandomNotes(this.StartTime, 0.37, 0.08, 0);
        }
        if (hasFlag(this.convertType, PatternType.LowProbability)) return this.generateNRandomNotes(this.StartTime, 0.17, 0, 0);
        return this.generateNRandomNotes(this.StartTime, 0.27, 0, 0);
    }

    generateRandomHoldNotes(startTime, noteCount) {
        const pattern = new Pattern();
        const usableColumns = this.TotalColumns - this.RandomStart - this.PreviousPattern.columnWithObjects;
        let nextColumn = this.getRandomColumn();
        for (let i = 0; i < Math.min(usableColumns, noteCount); i += 1) {
            nextColumn = this.findAvailableColumn(nextColumn, { patterns: [pattern, this.PreviousPattern] });
            this.addToPattern(pattern, nextColumn, startTime, this.EndTime);
        }
        for (let i = 0; i < noteCount - usableColumns; i += 1) {
            nextColumn = this.findAvailableColumn(nextColumn, { patterns: [pattern] });
            this.addToPattern(pattern, nextColumn, startTime, this.EndTime);
        }
        return pattern;
    }

    generateRandomNotes(startTime, noteCount) {
        const pattern = new Pattern();
        let nextColumn = this.getColumn(this.HitObject.x, true);
        if (hasFlag(this.convertType, PatternType.ForceNotStack) && this.PreviousPattern.columnWithObjects < this.TotalColumns) {
            nextColumn = this.findAvailableColumn(nextColumn, { patterns: [this.PreviousPattern] });
        }
        let lastColumn = nextColumn;
        for (let i = 0; i < noteCount; i += 1) {
            this.addToPattern(pattern, nextColumn, startTime, startTime);
            nextColumn = this.findAvailableColumn(nextColumn, { validation: (column) => column !== lastColumn });
            lastColumn = nextColumn;
            startTime += this.SegmentDuration;
        }
        return pattern;
    }

    generateStair(startTime) {
        const pattern = new Pattern();
        let column = this.getColumn(this.HitObject.x, true);
        let increasing = this.Random.nextDouble() > 0.5;
        for (let i = 0; i <= this.SpanCount; i += 1) {
            this.addToPattern(pattern, column, startTime, startTime);
            startTime += this.SegmentDuration;
            if (increasing) {
                if (column >= this.TotalColumns - 1) {
                    increasing = false;
                    column -= 1;
                } else {
                    column += 1;
                }
            } else if (column <= this.RandomStart) {
                increasing = true;
                column += 1;
            } else {
                column -= 1;
            }
        }
        return pattern;
    }

    generateRandomMultipleNotes(startTime) {
        const pattern = new Pattern();
        const legacy = this.TotalColumns >= 4 && this.TotalColumns <= 8;
        const interval = this.Random.nextIntRange(1, this.TotalColumns - (legacy ? 1 : 0));
        let nextColumn = this.getColumn(this.HitObject.x, true);
        for (let i = 0; i <= this.SpanCount; i += 1) {
            this.addToPattern(pattern, nextColumn, startTime, startTime);
            nextColumn += interval;
            if (nextColumn >= this.TotalColumns - this.RandomStart) {
                nextColumn = nextColumn - this.TotalColumns - this.RandomStart + (legacy ? 1 : 0);
            }
            nextColumn += this.RandomStart;
            if (this.TotalColumns > 2) this.addToPattern(pattern, nextColumn, startTime, startTime);
            nextColumn = this.getRandomColumn();
            startTime += this.SegmentDuration;
        }
        return pattern;
    }

    generateNRandomNotes(startTime, p2, p3, p4) {
        switch (this.TotalColumns) {
            case 2:
                p2 = 0; p3 = 0; p4 = 0;
                break;
            case 3:
                p2 = Math.min(p2, 0.1); p3 = 0; p4 = 0;
                break;
            case 4:
                p2 = Math.min(p2, 0.3); p3 = Math.min(p3, 0.04); p4 = 0;
                break;
            case 5:
                p2 = Math.min(p2, 0.34); p3 = Math.min(p3, 0.1); p4 = Math.min(p4, 0.03);
                break;
            default:
                break;
        }
        const headSamples = this.sampleInfoListAt(this.StartTime);
        const canGenerateTwoNotes = !hasFlag(this.convertType, PatternType.LowProbability)
            && (isDoubleSample(this.HitObject.samples) || isDoubleSample(headSamples));
        if (canGenerateTwoNotes) p2 = 1;
        return this.generateRandomHoldNotes(startTime, this.getRandomNoteCount(p2, p3, p4));
    }

    generateTiledHoldNotes(startTime) {
        const pattern = new Pattern();
        const columnRepeat = Math.min(this.SpanCount, this.TotalColumns);
        const endTime = startTime + this.SegmentDuration * this.SpanCount;
        let nextColumn = this.getColumn(this.HitObject.x, true);
        if (hasFlag(this.convertType, PatternType.ForceNotStack) && this.PreviousPattern.columnWithObjects < this.TotalColumns) {
            nextColumn = this.findAvailableColumn(nextColumn, { patterns: [this.PreviousPattern] });
        }
        for (let i = 0; i < columnRepeat; i += 1) {
            nextColumn = this.findAvailableColumn(nextColumn, { patterns: [pattern] });
            this.addToPattern(pattern, nextColumn, startTime, endTime);
            startTime += this.SegmentDuration;
        }
        return pattern;
    }

    generateHoldAndNormalNotes(startTime) {
        const pattern = new Pattern();
        let holdColumn = this.getColumn(this.HitObject.x, true);
        if (hasFlag(this.convertType, PatternType.ForceNotStack) && this.PreviousPattern.columnWithObjects < this.TotalColumns) {
            holdColumn = this.findAvailableColumn(holdColumn, { patterns: [this.PreviousPattern] });
        }
        this.addToPattern(pattern, holdColumn, startTime, this.EndTime);
        let nextColumn = this.getRandomColumn();
        let noteCount;
        if (this.conversionDifficulty > 6.5) noteCount = this.getRandomNoteCount(0.63, 0);
        else if (this.conversionDifficulty > 4) noteCount = this.getRandomNoteCount(this.TotalColumns < 6 ? 0.12 : 0.45, 0);
        else if (this.conversionDifficulty > 2.5) noteCount = this.getRandomNoteCount(this.TotalColumns < 6 ? 0 : 0.24, 0);
        else noteCount = 0;
        noteCount = Math.min(this.TotalColumns - 1, noteCount);
        const headSamples = this.sampleInfoListAt(startTime);
        const ignoreHead = !(headSamples.has(HIT_WHISTLE) || headSamples.has(HIT_FINISH) || headSamples.has(HIT_CLAP));
        const rowPattern = new Pattern();
        for (let i = 0; i <= this.SpanCount; i += 1) {
            if (!(ignoreHead && startTime === this.StartTime)) {
                for (let j = 0; j < noteCount; j += 1) {
                    nextColumn = this.findAvailableColumn(nextColumn, {
                        validation: (column) => column !== holdColumn,
                        patterns: [rowPattern],
                    });
                    this.addToPattern(rowPattern, nextColumn, startTime, startTime);
                }
            }
            pattern.addPattern(rowPattern);
            rowPattern.clear();
            startTime += this.SegmentDuration;
        }
        return pattern;
    }

    sampleInfoListAt(time) {
        const nodeSamples = this.nodeSamplesAt(time);
        if (nodeSamples && nodeSamples.length) return nodeSamples[0];
        return this.HitObject.samples;
    }

    nodeSamplesAt(time) {
        if (!this.nodeSamples) return null;
        const index = this.SegmentDuration === 0 ? 0 : Math.trunc((time - this.StartTime) / this.SegmentDuration);
        if (index <= 0) return this.nodeSamples;
        return this.nodeSamples.slice(index);
    }

    addToPattern(pattern, column, startTime, endTime) {
        if (startTime === endTime) {
            pattern.add({ column, startTime, endTime: startTime, samples: this.sampleInfoListAt(startTime) });
        } else {
            pattern.add({
                column,
                startTime,
                endTime,
                samples: this.HitObject.samples,
                nodeSamples: this.nodeSamplesAt(startTime),
            });
        }
    }
}

class SpinnerPatternGenerator extends LegacyPatternGenerator {
    constructor(random, hitObject, beatmap, totalColumns, previousPattern) {
        super(random, hitObject, beatmap, previousPattern, totalColumns);
        this.endTime = Math.trunc(hitObject.endTime || 0);
        this.convertType = this.PreviousPattern.columnWithObjects === this.TotalColumns
            ? PatternType.None
            : PatternType.ForceNotStack;
    }

    generate() {
        return [this.generatePattern()];
    }

    generatePattern() {
        const pattern = new Pattern();
        const generateHold = this.endTime - this.HitObject.startTime >= 100;
        if (this.TotalColumns === 8 && hasFinish(this.HitObject.samples) && this.endTime - this.HitObject.startTime < 1000) {
            this.addToPattern(pattern, 0, generateHold);
        } else if (this.TotalColumns === 8) {
            this.addToPattern(pattern, this.getRandomColumnForSpinner(null), generateHold);
        } else {
            this.addToPattern(pattern, this.getRandomColumnForSpinner(0), generateHold);
        }
        return pattern;
    }

    getRandomColumnForSpinner(lowerBound) {
        const getRandom = () => (lowerBound === null ? this.getRandomColumn() : this.getRandomColumn(lowerBound));
        if (hasFlag(this.convertType, PatternType.ForceNotStack)) {
            return this.findAvailableColumn(getRandom(), { lowerBound, patterns: [this.PreviousPattern] });
        }
        return this.findAvailableColumn(getRandom(), { lowerBound });
    }

    addToPattern(pattern, column, holdNote) {
        if (holdNote) {
            pattern.add({
                column,
                startTime: this.HitObject.startTime,
                endTime: this.endTime,
                samples: this.HitObject.samples,
            });
        } else {
            pattern.add({
                column,
                startTime: this.HitObject.startTime,
                endTime: this.HitObject.startTime,
                samples: this.HitObject.samples,
            });
        }
    }
}

function hitSoundToSamples(hitSound) {
    const samples = new Set([HIT_NORMAL]);
    if (hitSound & 2) samples.add(HIT_WHISTLE);
    if (hitSound & 4) samples.add(HIT_FINISH);
    if (hitSound & 8) samples.add(HIT_CLAP);
    return samples;
}

function edgeSoundsToNodeSamples(edgeSounds, fallbackSamples) {
    if (!edgeSounds || !edgeSounds.length) return null;
    return edgeSounds.map((value) => hitSoundToSamples(value));
}

function getColumnCount(difficulty, totalObjectCount, endTimeObjectCount) {
    const roundedCircleSize = roundHalfEven(difficulty.circleSize);
    const roundedOverallDifficulty = roundHalfEven(difficulty.overallDifficulty);

    if (totalObjectCount > 0 && endTimeObjectCount >= 0) {
        const percentSpecialObjects = totalObjectCount === 0 ? 0 : endTimeObjectCount / totalObjectCount;
        if (percentSpecialObjects < 0.2) return 7;
        if (percentSpecialObjects < 0.3 || roundedCircleSize >= 5) {
            return roundedOverallDifficulty > 5 ? 7 : 6;
        }
        if (percentSpecialObjects > 0.6) {
            return roundedOverallDifficulty > 4 ? 5 : 4;
        }
    }
    return Math.max(4, Math.min(Math.trunc(roundedOverallDifficulty) + 1, 7));
}

export function convertToMania({ objects, timingPoints, totalBreakTime, difficulty, sourceMode }) {
    const totalObjectCount = objects.length;
    const endTimeObjectCount = objects.filter((object) => object.slider || object.spinner).length;
    const columnCount = getColumnCount(difficulty, totalObjectCount, endTimeObjectCount);

    const sortedTiming = [...timingPoints].sort((a, b) => a.time - b.time);
    const beatLengthAt = (time) => {
        let beatLength = 500;
        for (const point of sortedTiming) {
            if (point.time > time) break;
            if (point.uninherited && point.beatLength > 0) beatLength = point.beatLength;
        }
        return beatLength;
    };
    const kiaiAt = (time) => {
        let kiai = false;
        for (const point of sortedTiming) {
            if (point.time > time) break;
            kiai = (point.effects & 1) !== 0;
        }
        return kiai;
    };
    const svAt = (time) => {
        let sv = 1;
        for (const point of sortedTiming) {
            if (point.time > time) break;
            if (!point.uninherited && point.beatLength < 0) {
                sv = clamp(-100 / point.beatLength, 0.01, 10);
            } else if (point.uninherited) {
                sv = 1;
            }
        }
        return sv;
    };
    const precisionAdjustedBeatLength = (time, sv) => {
        const base = beatLengthAt(time);
        const sliderVelocityAsBeatLength = -100 / sv;
        const bpmMultiplier = sliderVelocityAsBeatLength < 0
            ? clamp(Math.fround(-sliderVelocityAsBeatLength), 10, 10000) / 100
            : 1;
        return base * bpmMultiplier;
    };

    const beatmap = {
        hitObjects: objects.map((object) => ({
            startTime: object.time,
            x: object.x,
            y: object.y,
            samples: hitSoundToSamples(object.hitSound || 0),
        })),
        difficulty: {
            drainRate: difficulty.drainRate,
            approachRate: difficulty.approachRate,
            circleSize: difficulty.circleSize,
            overallDifficulty: difficulty.overallDifficulty,
            sliderMultiplier: difficulty.sliderMultiplier,
        },
        totalBreakTime,
        beatLengthAt,
        kiaiAt,
        svAt,
        precisionAdjustedBeatLength,
    };

    const seed = Math.trunc(roundHalfEven(difficulty.drainRate + difficulty.circleSize)) * 20
        + Math.trunc(difficulty.overallDifficulty * 41.2)
        + Math.trunc(roundHalfEven(difficulty.approachRate));
    const random = new LegacyRandom(seed);

    let previousPattern = new Pattern();
    let lastPattern = previousPattern;
    let lastTime = 0;
    let lastPosition = { x: 0, y: 0 };
    let density = Number.MAX_SAFE_INTEGER;
    let lastStair = PatternType.Stair;
    const prevNoteTimes = [];

    const computeDensity = (newNoteTime) => {
        prevNoteTimes.push(newNoteTime);
        if (prevNoteTimes.length > 7) prevNoteTimes.shift();
        if (prevNoteTimes.length >= 2) {
            density = (prevNoteTimes[prevNoteTimes.length - 1] - prevNoteTimes[0]) / prevNoteTimes.length;
        }
    };
    const recordNote = (time, position) => {
        lastTime = time;
        lastPosition = position;
    };

    const result = [];
    const accumulate = (pattern) => {
        for (const object of pattern.objects) result.push(object);
    };

    for (const object of objects) {
        const hitObject = {
            startTime: object.time,
            endTime: object.endTime,
            x: object.x,
            y: object.y,
            samples: hitSoundToSamples(object.hitSound || 0),
            repeats: object.repeats || 1,
            pixelLength: object.pixelLength || 0,
            hasSliderVelocity: sourceMode === 0 || sourceMode === 2,
            sv: svAt(object.time),
            nodeSamples: edgeSoundsToNodeSamples(object.edgeSounds, hitSoundToSamples(object.hitSound || 0)),
        };

        let generator;
        try {
            if (object.spinner) {
                generator = new SpinnerPatternGenerator(random, hitObject, beatmap, columnCount, lastPattern);
                recordNote(hitObject.endTime || hitObject.startTime, { x: 256, y: 192 });
                computeDensity(hitObject.endTime || hitObject.startTime);
            } else if (object.slider) {
                generator = new SliderPatternGenerator(random, hitObject, beatmap, columnCount, lastPattern);
                for (let i = 0; i <= generator.SpanCount; i += 1) {
                    const time = hitObject.startTime + generator.SegmentDuration * i;
                    recordNote(time, { x: hitObject.x, y: hitObject.y });
                    computeDensity(time);
                }
            } else {
                computeDensity(hitObject.startTime);
                generator = new HitCirclePatternGenerator(
                    random,
                    hitObject,
                    beatmap,
                    columnCount,
                    lastPattern,
                    lastTime,
                    lastPosition,
                    density,
                    lastStair,
                );
                recordNote(hitObject.startTime, { x: hitObject.x, y: hitObject.y });
            }

            for (const pattern of generator.generate()) {
                if (generator instanceof HitCirclePatternGenerator) lastStair = generator.StairType;
                if (generator instanceof HitCirclePatternGenerator || generator instanceof SliderPatternGenerator) {
                    lastPattern = pattern;
                }
                accumulate(pattern);
            }
        } catch (err) {
            if (!(err instanceof NotEnoughColumnsException)) throw err;
            const column = clamp(Math.floor((object.x * columnCount) / 512), 0, columnCount - 1);
            result.push({
                column,
                startTime: object.time,
                endTime: object.slider || object.spinner ? (object.endTime || object.time) : object.time,
                samples: hitObject.samples,
            });
            recordNote(object.time, { x: object.x, y: object.y });
        }
    }

    return { columnCount, objects: result };
}
